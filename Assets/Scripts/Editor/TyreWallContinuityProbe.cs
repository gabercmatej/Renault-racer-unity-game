using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// DOES THE TYRE WALL FOLLOW THE TRACK, AND IS THE WALL YOU SEE THE WALL YOU HIT?
    ///
    /// Maj saw tyre-border corners of about 90 degrees where the road does not turn
    /// sharply, and could drive through them with the game glitching. Both have one
    /// cause, and this probe measures it from the gameplay data both systems read:
    ///
    ///   [W1] the VISIBLE wall, per segment, both sides, exactly as RoadRenderer lays it
    ///        out (each segment's barrier at its start, straight across the segment to
    ///        the next one's). Every piece is graded on the angle it makes with the road
    ///        and on the kink against the piece before it.
    ///   [W2] the COLLISION boundary, found by putting the REAL CarSimulation just
    ///        outside and just inside the visible wall at four points in every segment,
    ///        both sides, and seeing whether it is clamped. Collision must equal the
    ///        visible wall everywhere, to +/-0.02 half-widths (22 units).
    ///   [W3] driving the real car into the wall: shallow, square, scraping, across
    ///        every place the run-off changes width, in the two tightest corners and
    ///        over the biggest crest. The car may never be inside the visible wall, and
    ///        may never be moved sideways faster than it could physically travel (a
    ///        teleport is what the "glitch" was).
    ///   [W4] reference, not gating: wall quads and fill per frame, and before/after
    ///        PNGs of the wall close up, mid-distance, on a curve and at a run-off
    ///        joint (needs a graphics device; skipped under -nographics).
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///     -executeMethod RenaultRacer.EditorTools.TyreWallContinuityProbe.RunFromCommandLine
    /// </summary>
    public static class TyreWallContinuityProbe
    {
        private const float Step = 1f / 60f;
        private const string ProfilePath = "Assets/ScriptableObjects/Handling/Production.asset";
        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";
        private const string LogPath = "Logs/tyre-wall-probe.txt";

        /// <summary>A wall piece steeper than this against the road reads as a corner.</summary>
        public const float MaxWallAngleDeg = 20f;
        /// <summary>Change of direction between two adjacent wall pieces.</summary>
        public const float MaxKinkDeg = 6f;
        /// <summary>How far the collision boundary may sit from the visible wall.</summary>
        public const float BoundaryTolerance = 0.02f;
        /// <summary>Deepest the car's centre may get past the visible wall line while driving.</summary>
        public const float MaxPenetration = 0.01f;

        private const float OrthoSize = 5f;
        private const float Aspect = 16f / 9f;

        [MenuItem("Renault Racer/Tyre wall continuity probe", false, 47)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e)
            {
                Console.WriteLine("[tyre wall probe] threw: " + e);
                EditorApplication.Exit(2);
                return;
            }
            Console.WriteLine(report);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, report);
            bool clean = !report.Contains("**FAIL**");
            Console.WriteLine(clean ? "[tyre wall probe] ALL PASS" : "[tyre wall probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        // ------------------------------------------------------------------ the wall

        /// <summary>
        /// The visible wall line for the car's CENTRE at distance z, in half-widths, on
        /// one side (-1 left, +1 right), signed. RoadRenderer draws the tyre face at each
        /// segment's barrier (plus railOutset, half the car's drawn width - so the body
        /// meets the face exactly when the centre meets this line) at the segment's start
        /// and runs it straight to the next segment's: linear across the segment.
        /// </summary>
        public static float VisibleWall(TrackModel t, float z, int side)
        {
            int i = t.SegmentIndexAt(z);
            float f = t.SegmentPercentAt(z);
            Segment a = t[i], b = t[i + 1];
            float la = side < 0 ? a.barrierLeft : a.barrierRight;
            float lb = side < 0 ? b.barrierLeft : b.barrierRight;
            return la + (lb - la) * f;
        }

        private static string Where(TrackModel t, int seg)
        {
            TurnInfo[] turns = t.Layout.Turns;
            if (turns == null || turns.Length == 0) return "seg " + seg;
            string best = null; int bestD = int.MaxValue;
            for (int k = 0; k < turns.Length; k++)
            {
                TurnInfo tu = turns[k];
                string name = "T" + tu.number.ToString("00");
                if (seg >= tu.startSegment && seg < tu.endSegment)
                    return name + " +" + (seg - tu.startSegment) + " (of " + (tu.endSegment - tu.startSegment) + ")";
                int d = seg < tu.startSegment ? tu.startSegment - seg : seg - tu.endSegment + 1;
                if (d < bestD)
                {
                    bestD = d;
                    best = seg < tu.startSegment ? d + " before " + name : d + " after " + name;
                }
            }
            return best;
        }

        public static string Run()
        {
            HandlingProfile p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(ProfilePath);
            if (p == null) return "**FAIL** no Production profile at " + ProfilePath;
            TrackModel track = TrackBuilder.BuildMainTrack();

            var sb = new StringBuilder(16384);
            sb.AppendLine("================ TYRE WALL CONTINUITY PROBE ================");
            sb.AppendLine("main circuit, " + track.Count + " segments of " + RoadGeometry.SegmentLength
                          + " units, road half-width " + RoadGeometry.RoadHalfWidth);
            sb.AppendLine("thresholds: wall angle <= " + MaxWallAngleDeg + " deg, kink <= " + MaxKinkDeg
                          + " deg, collision = visible +/-" + BoundaryTolerance + ", driving penetration <= "
                          + MaxPenetration + " half-widths, no lateral teleport");
            sb.AppendLine();

            bool all = true;
            all &= Geometry(track, sb);
            all &= Boundary(track, p, sb);
            all &= Driving(track, p, sb);
            Reference(track, sb);

            sb.AppendLine("TYRE WALL FOLLOWS THE TRACK AND IS THE WALL YOU HIT = " + Pass(all));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ [W1]

        private static bool Geometry(TrackModel t, StringBuilder sb)
        {
            sb.AppendLine("[W1] VISIBLE WALL GEOMETRY, every segment, both sides");
            float seg = RoadGeometry.SegmentLength, hw = RoadGeometry.RoadHalfWidth;
            float worstAngle = 0f, worstKink = 0f;
            int bad = 0, widthChanges = 0;
            var lines = new List<string>();
            for (int side = -1; side <= 1; side += 2)
            {
                float prevSlope = 0f;
                for (int i = 0; i <= t.Count; i++)
                {
                    Segment a = t[i], b = t[i + 1];
                    float la = side < 0 ? a.barrierLeft : a.barrierRight;
                    float lb = side < 0 ? b.barrierLeft : b.barrierRight;
                    float slope = Mathf.Atan((lb - la) * hw / seg);   // vs the road tangent
                    float angle = Mathf.Abs(slope) * Mathf.Rad2Deg;
                    if (lb != la) widthChanges++;
                    // The road's own turn across one segment is common to both sides and
                    // to the wall; it is part of the kink a wall piece makes with the one
                    // before it, so it is included.
                    float kink = i == 0 ? 0f
                               : Mathf.Abs(slope - prevSlope + t[i].curve / seg) * Mathf.Rad2Deg;
                    prevSlope = slope;
                    if (i == t.Count) continue;      // wrap row, only for the kink at 0
                    worstAngle = Mathf.Max(worstAngle, angle);
                    worstKink = Mathf.Max(worstKink, kink);
                    if (angle > MaxWallAngleDeg || kink > MaxKinkDeg)
                    {
                        bad++;
                        if (lines.Count < 40)
                            lines.Add(string.Format("    {0} seg {1,4} ({2}): {3:F2} -> {4:F2}  angle {5,5:F1} deg  kink {6,5:F1} deg",
                                side < 0 ? "LEFT " : "RIGHT", i, Where(t, i), Mathf.Abs(la), Mathf.Abs(lb), angle, kink));
                    }
                }
            }
            foreach (string l in lines) sb.AppendLine(l + "   **FAIL**");
            if (bad > lines.Count) sb.AppendLine("    ... " + (bad - lines.Count) + " more");
            bool ok = bad == 0;
            sb.AppendLine("    pieces that change width " + widthChanges + "   bad joints " + bad
                          + "   worst wall angle " + worstAngle.ToString("F1") + " deg   worst kink "
                          + worstKink.ToString("F1") + " deg   " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        // ------------------------------------------------------------------ [W2]

        private static bool Boundary(TrackModel t, HandlingProfile p, StringBuilder sb)
        {
            sb.AppendLine("[W2] COLLISION BOUNDARY = VISIBLE WALL (real CarSimulation, 4 points per segment, both sides)");
            float[] fr = { 0.02f, 0.25f, 0.5f, 0.75f, 0.98f };
            int tests = 0, through = 0, invisible = 0;
            float worstThrough = 0f, worstInvisible = 0f;
            var lines = new List<string>();
            var inp = new HandlingProbe.ScriptedInput();
            var sim = new CarSimulation(t, p);
            for (int i = 0; i < t.Count; i++)
                for (int k = 0; k < fr.Length; k++)
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float z = (i + fr[k]) * RoadGeometry.SegmentLength;
                        float vis = VisibleWall(t, z, side);

                        // Just OUTSIDE the visible wall: must be pushed back onto it.
                        sim.Reset(z);
                        sim.X = vis + side * BoundaryTolerance;
                        sim.Step(Step, inp);
                        float visAfter = VisibleWall(t, sim.Z, side);
                        float past = (sim.X - visAfter) * side;
                        if (past > 0.002f)
                        {
                            through++;
                            worstThrough = Mathf.Max(worstThrough, past);
                            if (lines.Count < 30)
                                lines.Add(string.Format("    THROUGH   {0} seg {1,4} +{2:F2} ({3}): car centre {4:F3} past the visible wall {5:F3}",
                                    side < 0 ? "LEFT " : "RIGHT", i, fr[k], Where(t, i), past, Mathf.Abs(vis)));
                        }
                        // Just INSIDE it: must be left alone.
                        sim.Reset(z);
                        sim.X = vis - side * BoundaryTolerance;
                        float x0 = sim.X;
                        sim.Step(Step, inp);
                        float pushed = (x0 - sim.X) * side;
                        if (pushed > 0.002f)
                        {
                            invisible++;
                            worstInvisible = Mathf.Max(worstInvisible, pushed);
                            if (lines.Count < 30)
                                lines.Add(string.Format("    INVISIBLE {0} seg {1,4} +{2:F2} ({3}): clamped {4:F3} short of the visible wall {5:F3}",
                                    side < 0 ? "LEFT " : "RIGHT", i, fr[k], Where(t, i), pushed + BoundaryTolerance, Mathf.Abs(vis)));
                        }
                        tests++;
                    }
            foreach (string l in lines) sb.AppendLine(l + "   **FAIL**");
            if (through + invisible > lines.Count) sb.AppendLine("    ... " + (through + invisible - lines.Count) + " more");
            bool ok = through == 0 && invisible == 0;
            sb.AppendLine("    " + tests + " points: drive-through " + through + " (worst " + worstThrough.ToString("F3")
                          + ")   invisible wall " + invisible + " (worst " + (invisible > 0 ? (worstInvisible + BoundaryTolerance).ToString("F3") : "0")
                          + ")   " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        // ------------------------------------------------------------------ [W3]

        private struct DriveResult
        {
            public float penetration, teleport, minSpeed, endSpeed, backZ, maxYaw;
            public int contactSteps, impacts;
        }

        /// <summary>Drive the real simulation; measure against the VISIBLE wall every step.</summary>
        private static DriveResult Drive(TrackModel t, HandlingProfile p, float z, float x, float yawDeg,
                                         float speedPct, float steer, float seconds,
                                         float lateral = 0f)
        {
            var sim = new CarSimulation(t, p);
            var inp = new HandlingProbe.ScriptedInput { Steer = steer };
            sim.Reset(z);
            sim.X = x;
            sim.Speed = p.maxSpeed * speedPct;
            sim.PointAt(yawDeg * Mathf.Deg2Rad);
            if (lateral != 0f) sim.SetLateralVelocity(lateral);
            DriveResult r = new DriveResult { minSpeed = 9f };
            int n = Mathf.RoundToInt(seconds / Step);
            for (int s = 0; s < n; s++)
            {
                float xb = sim.X, zb = sim.Z, spd = sim.Speed;
                sim.Step(Step, inp);
                if (sim.ConsumeImpact() > 0f) r.impacts++;
                // Nothing moves the car sideways faster than the car can travel. More
                // than that is a teleport - the glitch Maj saw at the joints.
                float maxLat = Mathf.Max(spd, sim.Speed) * Step / RoadGeometry.RoadHalfWidth + 0.002f;
                r.teleport = Mathf.Max(r.teleport, Mathf.Abs(sim.X - xb) - maxLat);
                for (int side = -1; side <= 1; side += 2)
                {
                    float vis = VisibleWall(t, sim.Z, side);
                    float past = (sim.X - vis) * side;
                    r.penetration = Mathf.Max(r.penetration, past);
                    if (past > -CarSimulation.BarrierContactBand) r.contactSteps++;
                }
                if (sim.Z < zb) r.backZ += zb - sim.Z;
                r.minSpeed = Mathf.Min(r.minSpeed, sim.SpeedPct);
                r.maxYaw = Mathf.Max(r.maxYaw, Mathf.Abs(sim.Yaw) * Mathf.Rad2Deg);
            }
            r.endSpeed = sim.SpeedPct;
            return r;
        }

        private static bool Grade(StringBuilder sb, string name, DriveResult r, bool needContact, float minEndSpeed)
        {
            bool ok = r.penetration <= MaxPenetration && r.teleport <= 0f && r.backZ <= 0f
                   && r.maxYaw <= FullLockProbe.MaxYawDeg && r.endSpeed >= minEndSpeed
                   && (!needContact || r.contactSteps > 0);
            sb.AppendLine(string.Format(
                "    {0,-46} past wall {1,6:F3}  teleport {2,6:F3}  contact {3,4} steps  impacts {4,2}  min/end speed {5:F2}/{6:F2}  |yaw| {7,4:F0}  back {8:F0}   {9}",
                name, Mathf.Max(0f, r.penetration), Mathf.Max(0f, r.teleport), r.contactSteps, r.impacts,
                r.minSpeed, r.endSpeed, r.maxYaw, r.backZ, Pass(ok)));
            return ok;
        }

        private static bool Driving(TrackModel t, HandlingProfile p, StringBuilder sb)
        {
            sb.AppendLine("[W3] DRIVING THE REAL CAR INTO THE WALL");
            bool all = true;
            TurnInfo[] turns = t.Layout.Turns;

            // A plain straight: the middle of the back straight before T05.
            int straight = turns[4].startSegment - 80;
            float zS = straight * RoadGeometry.SegmentLength;
            float wallR = VisibleWall(t, zS, 1);
            all &= Grade(sb, "shallow contact, 3 deg + 0.6 hw/s, straight", Drive(t, p, zS, wallR - 0.05f, 3f, 0.9f, 0f, 3f, 0.6f), true, 0.5f);
            all &= Grade(sb, "direct impact, 35 deg, straight", Drive(t, p, zS, wallR - 0.4f, 35f, 0.9f, 0f, 3f), true, 0.15f);
            all &= Grade(sb, "scraping, full lock into it 6 s, straight", Drive(t, p, zS, wallR - 0.3f, 0f, 0.9f, 1f, 6f), true, 0.15f);
            all &= Grade(sb, "scraping LEFT, full lock 6 s, straight", Drive(t, p, zS, -wallR + 0.3f, 0f, 0.9f, -1f, 6f), true, 0.15f);

            // Every place the run-off changes width: hug the wall that moves and drive
            // through the change, at race speed and slowly, in both cases leaning on it.
            var joints = new List<Vector2Int>();   // (segment, side)
            for (int side = -1; side <= 1; side += 2)
            {
                for (int i = 0; i < t.Count; i++)
                {
                    float a = side < 0 ? t[i].barrierLeft : t[i].barrierRight;
                    float b = side < 0 ? t[i + 1].barrierLeft : t[i + 1].barrierRight;
                    float c = side < 0 ? t[i - 1].barrierLeft : t[i - 1].barrierRight;
                    // The first segment of every run where the width changes.
                    if (a != b && c == a) joints.Add(new Vector2Int(i, side));
                }
            }
            int transitions = 0;
            foreach (Vector2Int jt in joints)
            {
                int i = jt.x, side = jt.y;
                float z0 = (i - 25) * RoadGeometry.SegmentLength;
                float x0 = VisibleWall(t, z0, side) - side * 0.05f;
                float from = Mathf.Abs(side < 0 ? t[i].barrierLeft : t[i].barrierRight);
                                // ...and where it settles: the first segment after which it stops moving.
                int k = i + 1;
                while (k < i + 200 && (side < 0 ? t[k].barrierLeft != t[k + 1].barrierLeft
                                                : t[k].barrierRight != t[k + 1].barrierRight)) k++;
                float to = Mathf.Abs(side < 0 ? t[k].barrierLeft : t[k].barrierRight);
                string where = (side < 0 ? "L " : "R ") + Where(t, i) + " " + from.ToString("F2") + "->" + to.ToString("F2");
                // Race speed, steering gently into the wall the whole way through.
                all &= Grade(sb, "joint " + where + " fast", Drive(t, p, z0, x0, 0f, 0.85f, side * 0.35f, 4f), true, 0.15f);
                // Slow, full lock into it.
                all &= Grade(sb, "joint " + where + " slow lock", Drive(t, p, z0, x0, 0f, 0.3f, side, 6f), true, 0.10f);
                transitions++;
            }
            if (transitions == 0) sb.AppendLine("    (no run-off width changes on this circuit)");

            // The two tightest corners, carried in too fast: straight on into the outside.
            for (int k = 0; k < turns.Length; k++)
            {
                if (turns[k].number != 5 && turns[k].number != 14) continue;
                float z = (turns[k].holdSegment - 5) * RoadGeometry.SegmentLength;
                int outside = turns[k].curve > 0f ? -1 : 1;
                all &= Grade(sb, "T" + turns[k].number.ToString("00") + " too fast, no steer, into the outside",
                             Drive(t, p, z, 0f, 0f, 0.95f, 0f, 5f), true, 0.10f);
                all &= Grade(sb, "T" + turns[k].number.ToString("00") + " scraping the outside, lock outward",
                             Drive(t, p, z, VisibleWall(t, z, outside) - outside * 0.1f, 0f, 0.8f, outside, 5f), true, 0.10f);
            }

            // The biggest crest: where the road's slope drops the most over 10 segments.
            int crest = 0; float drop = 0f;
            for (int i = 10; i < t.Count - 10; i++)
            {
                float before = t[i].y - t[i - 10].y, after = t[i + 10].y - t[i].y;
                if (before - after > drop) { drop = before - after; crest = i; }
            }
            float zc = (crest - 15) * RoadGeometry.SegmentLength;
            all &= Grade(sb, "crest at seg " + crest + " (" + Where(t, crest) + "), scraping R",
                         Drive(t, p, zc, VisibleWall(t, zc, 1) - 0.1f, 0f, 0.9f, 1f, 4f), true, 0.15f);
            all &= Grade(sb, "crest at seg " + crest + ", scraping L",
                         Drive(t, p, zc, VisibleWall(t, zc, -1) + 0.1f, 0f, 0.9f, -1f, 4f), true, 0.15f);

            sb.AppendLine("    run-off width changes driven through: " + transitions);
            sb.AppendLine("    THE CAR NEVER PASSES THE WALL IT CAN SEE AND IS NEVER TELEPORTED = " + Pass(all));
            sb.AppendLine();
            return all;
        }

        // ------------------------------------------------------------------ [W4]

        private sealed class Rig
        {
            public GameObject cameraGo, roadGo;
            public Camera camera;
            public RoadRenderer road;
            public void Destroy()
            {
                if (roadGo != null) UnityEngine.Object.DestroyImmediate(roadGo);
                if (cameraGo != null) UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        private static Rig BuildRig(TrackModel track)
        {
            var r = new Rig();
            r.cameraGo = new GameObject("ProbeCamera");
            r.cameraGo.tag = "MainCamera";
            r.camera = r.cameraGo.AddComponent<Camera>();
            r.camera.orthographic = true;
            r.camera.orthographicSize = OrthoSize;
            r.camera.aspect = Aspect;
            // Everything is drawn on z = 0: stand back from it.
            r.camera.transform.position = new Vector3(0f, 0f, -10f);
            r.camera.nearClipPlane = 0.3f;
            r.camera.farClipPlane = 100f;
            r.roadGo = new GameObject("ProbeRoad", typeof(MeshFilter), typeof(MeshRenderer));
            r.road = r.roadGo.AddComponent<RoadRenderer>();
            r.road.Initialise(track, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));
            return r;
        }

        private static void WallStats(RoadRenderer road, out int quads, out float fill)
        {
            quads = 0; fill = 0f;
            Transform w = road.transform.Find("TyreWall");
            if (w == null) return;
            Mesh m = w.GetComponent<MeshFilter>().sharedMesh;
            if (m == null) return;
            Vector3[] v = m.vertices;
            int[] ix = m.GetIndices(0);
            float halfH = OrthoSize, halfW = OrthoSize * Aspect;
            for (int k = 0; k + 2 < ix.Length; k += 3)
            {
                Vector3 a = v[ix[k]], b = v[ix[k + 1]], c = v[ix[k + 2]];
                // Clip roughly to the view: count only triangles whose centroid is on screen.
                Vector3 g = (a + b + c) / 3f;
                if (Mathf.Abs(g.x) > halfW || Mathf.Abs(g.y) > halfH) continue;
                fill += Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f;
            }
            quads = ix.Length / 6;
            fill /= (4f * halfH * halfW);
        }

        private static void Reference(TrackModel t, StringBuilder sb)
        {
            sb.AppendLine("[W4] REFERENCE (not gating): wall cost and captures");
            TurnInfo[] turns = t.Layout.Turns;
            int straight = turns[4].startSegment - 80;
            // A run-off joint: the T01 widening, looked at from just before it.
            int joint = turns[0].startSegment - 22;
            var views = new[]
            {
                new { name = "close",   seg = straight, x = 2.0f },
                new { name = "mid",     seg = straight, x = 0f },
                new { name = "curve",   seg = turns[4].startSegment + 6, x = 0f },
                new { name = "joint",   seg = joint, x = -1.2f },
                new { name = "crest",   seg = turns[7].startSegment - 30, x = 0.6f },
            };
            Rig r = BuildRig(t);
            try
            {
                bool gpu = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
                foreach (var v in views)
                {
                    r.road.CameraZ = v.seg * RoadGeometry.SegmentLength + 37f;
                    r.road.CameraX = v.x;
                    r.road.Rebuild(Aspect);
                    int quads; float fill;
                    WallStats(r.road, out quads, out fill);
                    string png = gpu ? Capture(r, "tyrewall-" + v.name) : "(no graphics device)";
                    sb.AppendLine(string.Format("    {0,-6} seg {1,4} ({2}) camX {3,5:F2}: wall quads {4,4}  wall fill {5,5:F1}% of screen  {6}",
                        v.name, v.seg, Where(t, v.seg), v.x, quads, fill * 100f, png));
                }
            }
            finally { r.Destroy(); }
            sb.AppendLine();
        }

        private static string Capture(Rig r, string name)
        {
            Directory.CreateDirectory("Logs");
            var rt = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
            try
            {
                r.camera.targetTexture = rt;
                r.camera.clearFlags = CameraClearFlags.SolidColor;
                r.camera.backgroundColor = new Color(0.28f, 0.6f, 0.86f);
                r.camera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                string path = "Logs/" + name + ".png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return path;
            }
            finally
            {
                r.camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
