using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Effects;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Are skid marks actually DRAWN, and where? Editor-only, never ships.
    ///
    /// WHY THIS EXISTS. "Skid marks are not appearing" survived three phases because
    /// every check available was the wrong one. HandlingProbe's [9] asks whether the
    /// simulation reaches the slip that ought to lay rubber - it does, and it passed all
    /// along. The debug HUD counted marks STORED and quads BUILT - both non-zero. None of
    /// that says the trail is on the screen: a mesh built correctly and projected off the
    /// bottom of the frame, or built at alpha 0.02, produces exactly those numbers and
    /// exactly no skid marks.
    ///
    /// So this drives the REAL RoadRenderer projection and the REAL SkidMarks mesh
    /// builder, then measures the quads against the camera's own frustum: how many land
    /// on screen, how opaque they are, and where the trail sits relative to the car. That
    /// is the question the player is asking.
    ///
    /// Headless: no GPU work happens: meshes are built on the CPU and the camera is only
    /// used for its orthographic bounds, which is why this can answer in batch mode a
    /// question a browser at one frame per second cannot.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///     -executeMethod RenaultRacer.EditorTools.SkidMarkProbe.RunFromCommandLine
    /// </summary>
    public static class SkidMarkProbe
    {
        private const float Step = 1f / 60f;
        private const string ProfilePath = "Assets/ScriptableObjects/Handling/Production.asset";
        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";
        private const string SkidMatPath = "Assets/Art/Road/SkidOverlay.mat";
        private const string LogPath = "Logs/skid-mark-probe.txt";

        // The scene's own values (Main.unity), so this measures what ships.
        private const float OrthoSize = 5f;
        private const float Aspect = 16f / 9f;
        private const float DistanceAhead = 1700f;   // CarView.distanceAhead

        [MenuItem("Renault Racer/Skid mark probe (Phase 10)", false, 46)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e)
            {
                Console.WriteLine("[SkidMarkProbe] threw: " + e);
                EditorApplication.Exit(2);
                return;
            }
            Console.WriteLine(report);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, report);
            EditorApplication.Exit(report.Contains("**FAIL**") ? 1 : 0);
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        /// <summary>Verdict for a row that is reported but does not gate the exit code.</summary>
        private static string Read(bool ok) { return ok ? "ok" : "below"; }

        /// <summary>Render the rig to Logs/, with and without the trail, so the two can
        /// be compared. Needs a real graphics device, so it is skipped under -nographics
        /// rather than failing the probe.</summary>
        private static void Capture(Rig r, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return;
            Directory.CreateDirectory("Logs");
            var rt = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
            var skidRenderer = r.skidGo.GetComponent<MeshRenderer>();
            try
            {
                r.camera.targetTexture = rt;
                r.camera.clearFlags = CameraClearFlags.SolidColor;
                r.camera.backgroundColor = new Color(0.28f, 0.6f, 0.86f);
                foreach (bool withTrail in new[] { true, false })
                {
                    if (skidRenderer != null) skidRenderer.enabled = withTrail;
                    r.camera.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    string slug = name.Replace(' ', '-').Replace(",", "");
                    File.WriteAllBytes("Logs/skid-" + slug + (withTrail ? "-with" : "-without") + ".png",
                                       tex.EncodeToPNG());
                }
                if (skidRenderer != null) skidRenderer.enabled = true;
            }
            finally
            {
                r.camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private sealed class Rig
        {
            public GameObject cameraGo, roadGo, skidGo;
            public Camera camera;
            public RoadRenderer road;
            public SkidMarks skid;

            public void Destroy()
            {
                if (skidGo != null) UnityEngine.Object.DestroyImmediate(skidGo);
                if (roadGo != null) UnityEngine.Object.DestroyImmediate(roadGo);
                if (cameraGo != null) UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        private static Rig BuildRig(TrackModel track)
        {
            var r = new Rig();

            // Camera.main, which RoadRenderer and SkidMarks both look up.
            r.cameraGo = new GameObject("ProbeCamera");
            r.cameraGo.tag = "MainCamera";
            r.camera = r.cameraGo.AddComponent<Camera>();
            r.camera.orthographic = true;
            r.camera.orthographicSize = OrthoSize;
            r.camera.aspect = Aspect;
            // Everything is drawn on the z = 0 plane, so the camera has to stand back
            // from it or the whole scene sits behind the near plane.
            r.camera.transform.position = new Vector3(0f, 0f, -10f);
            r.camera.nearClipPlane = 0.3f;
            r.camera.farClipPlane = 100f;

            r.roadGo = new GameObject("ProbeRoad", typeof(MeshFilter), typeof(MeshRenderer));
            r.road = r.roadGo.AddComponent<RoadRenderer>();
            r.road.Initialise(track, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));

            r.skidGo = new GameObject("ProbeSkid");
            r.skid = r.skidGo.AddComponent<SkidMarks>();
            // Awake has already run with a null roadRenderer; wire it and re-initialise,
            // exactly as the scene does through its serialised references.
            typeof(SkidMarks).GetField("roadRenderer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(r.skid, r.road);
            r.skid.Initialise(AssetDatabase.LoadAssetAtPath<Material>(SkidMatPath));
            // CarController does this at startup, so the trail is drawn out to wherever
            // the car is drawn. Without it the probe would cut the newest marks at the
            // component's own default of 1500 and measure a trail that stops short of a
            // car sitting at 1700.
            r.skid.maxDrawDistance = DistanceAhead + 100f;
            return r;
        }

        /// <summary>
        /// One frame of what CarController.Update does, in the same order: follow the
        /// camera, rebuild the road, record the slip, rebuild the trail.
        /// </summary>
        private static void Frame(Rig r, CarSimulation sim)
        {
            r.road.CameraZ = sim.Z;
            r.road.CameraX = sim.X;
            r.road.Rebuild(Aspect);
            r.skid.Record(sim.X, sim.Z + DistanceAhead, sim.SlipRatio,
                          sim.DriftAmount, sim.SpeedPct, sim.OffRoad);
            r.skid.Rebuild(DistanceAhead);
        }

        public static string Run()
        {
            HandlingProfile p = AssetDatabase.LoadAssetAtPath<HandlingProfile>(ProfilePath);
            if (p == null) return "**FAIL** no Production profile at " + ProfilePath;

            var sb = new StringBuilder(4096);
            sb.AppendLine("================ SKID MARK PROBE ================");
            sb.AppendLine("camera ortho " + OrthoSize + " aspect " + Aspect.ToString("F3")
                          + "   car drawn " + DistanceAhead + " units ahead");
            sb.AppendLine();

            // THE way the car is made to slide on tarmac. This gates, and under the
            // heading-spring model it is the only thing that can: the handbrake gesture
            // is the sole entry into a drift, so it is the sole thing that can lay
            // rubber. If this scenario ever stops marking, the skid marks are broken.
            bool all = true;
            all &= Scenario(p, sb, "handbrake drift on a straight", true, 0f, false);

            // And the ways it is NOT made to slide, recorded because they are the cases
            // everyone assumes should leave rubber and neither can.
            //
            // Trail-braking was a GATING case under the previous model, where braking
            // into lock provoked a slide on its own (measured then: peak slip 0.23,
            // drift 1.00, rubber on 68% of tarmac frames). The heading-spring model
            // deliberately removed every involuntary route into a drift - steering,
            // braking and surface alike - because each one read as the car losing
            // control by itself, and HandlingProbe [23] now ASSERTS that three seconds
            // of brake and full lock produces a drift of exactly zero. A gating skid
            // test that demands marks from brake-plus-lock therefore asserts the exact
            // opposite of an asserted property of the car. It is not loosened here -
            // the 0.06 slip and 0.40 drift thresholds are untouched, and nothing was
            // changed to make marks appear during ordinary cornering. It simply stops
            // gating, and stays printed, because what it now measures is the handling
            // model's intent rather than the skid marks' health.
            //
            // Full lock alone never marked under either model. On dry tarmac the
            // shipping profile's drift demand at full lock and full speed comes to about
            // 0.018 against a threshold of 0.024, so steering alone does not break the
            // tyres loose: the car understeers, washes wide, and only starts sliding once
            // it is on the grass - where rubber is deliberately not laid. Not a defect in
            // the marks; it is the answer to "why can I not see any".
            sb.AppendLine("--- reference, not gating ---");
            Scenario(p, sb, "trail-brake into full lock", false, 1f, true, false);
            Scenario(p, sb, "full lock alone, no brake", false, 1f, false, false);

            sb.AppendLine("SKID MARKS ARE DRAWN WHERE THEY CAN BE SEEN = " + Pass(all));
            return sb.ToString();
        }

        /// <summary>
        /// Run one provocation and report what the trail did.
        ///
        /// <paramref name="gating"/> decides how the verdict is SPELLED, not how it is
        /// measured: a reference row prints ok/below instead of PASS/**FAIL**. Without
        /// that the batch runner, whose definition of a failure is the report containing
        /// "**FAIL**", exits non-zero on a report whose own verdict line says PASS -
        /// which is exactly what this probe did, on this branch and on the one before
        /// it, for as long as it has had a reference row.
        /// </summary>
        private static bool Scenario(HandlingProfile p, StringBuilder sb, string name,
                                     bool handbrake, float steer, bool brake,
                                     bool gating = true)
        {
            TrackModel track = HandlingProbe.FlatTrack(4000);
            Rig r = BuildRig(track);
            try
            {
                var sim = new CarSimulation(track, p);
                sim.Reset(0f);
                var inp = new HandlingProbe.ScriptedInput();

                // Up to speed on the straight, hands off.
                for (int i = 0; i < 60 * 8; i++) { sim.Step(Step, inp); Frame(r, sim); }
                float entrySpeed = sim.SpeedPct;

                // Provoke, and hold it for a second of real sliding.
                inp.Handbrake = handbrake;
                inp.Steer = steer;
                inp.Brake = brake;

                int frames = 60 * 2;
                int peakQuads = 0, peakSeen = 0, everOnRoad = 0, laying = 0;
                float peakAlpha = 0f, nearest = 0f, furthest = 0f, minY = 0f, maxY = 0f;
                float peakSlip = 0f, peakDrift = 0f;
                // The numbers that decide whether rubber goes down AT ALL, measured only
                // while the tyres are on tarmac - which is the only place it is laid.
                float tarmacSlip = 0f, tarmacDrift = 0f;
                for (int i = 0; i < frames; i++)
                {
                    sim.Step(Step, inp);
                    Frame(r, sim);
                    if (!sim.OffRoad)
                    {
                        everOnRoad++;
                        tarmacSlip = Mathf.Max(tarmacSlip, sim.SlipRatio);
                        tarmacDrift = Mathf.Max(tarmacDrift, sim.DriftAmount);
                        if (sim.SlipRatio > r.skid.slipThreshold || sim.DriftAmount > r.skid.driftThreshold)
                            laying++;
                    }
                    peakSlip = Mathf.Max(peakSlip, sim.SlipRatio);
                    peakDrift = Mathf.Max(peakDrift, sim.DriftAmount);
                    if (r.skid.LastOnScreenQuads > peakSeen)
                    {
                        peakSeen = r.skid.LastOnScreenQuads;
                        nearest = r.skid.LastNearDepth;
                        furthest = r.skid.LastFarDepth;
                        minY = r.skid.LastMinY;
                        maxY = r.skid.LastMaxY;
                        // Picture the BEST frame, not the last one. By the end of a
                        // two-second slide the car has usually left the road, and a
                        // photograph of that says nothing about the trail.
                        Capture(r, name);
                    }
                    peakQuads = Mathf.Max(peakQuads, r.skid.LastQuadCount);
                    peakAlpha = Mathf.Max(peakAlpha, r.skid.LastPeakAlpha);
                }

                // Where the CAR is drawn, so the trail can be judged against it.
                Vector3 carPos; float carScale;
                r.road.TryProjectRoadPoint(sim.X, DistanceAhead, out carPos, out carScale);
                float halfH = OrthoSize, halfW = OrthoSize * Aspect;

                bool textured = r.skid.Textured;
                bool marksStored = r.skid.MarkCount >= 2;
                bool quadsBuilt = peakQuads > 0;
                bool onScreen = peakSeen > 0;
                bool opaque = peakAlpha >= 0.2f;
                bool behindCar = onScreen && maxY >= carPos.y - 0.05f;

                bool ok = textured && marksStored && quadsBuilt && onScreen && opaque && behindCar;

                sb.AppendLine("[" + name + "]");
                sb.AppendLine("    entry " + (entrySpeed * 220f).ToString("F0") + " km/h"
                              + "   on tarmac " + (everOnRoad * 100 / frames) + "% of the slide"
                              + "   peak slip " + peakSlip.ToString("F2")
                              + "   peak drift " + peakDrift.ToString("F2"));
                sb.AppendLine("    ON TARMAC: peak slip " + tarmacSlip.ToString("F2")
                              + " (threshold " + r.skid.slipThreshold.ToString("F2") + ")"
                              + "   peak drift " + tarmacDrift.ToString("F2")
                              + " (threshold " + r.skid.driftThreshold.ToString("F2") + ")"
                              + "   laying rubber " + (everOnRoad > 0 ? laying * 100 / everOnRoad : 0)
                              + "% of tarmac frames");
                sb.AppendLine("    marks stored " + r.skid.MarkCount
                              + "   quads built " + peakQuads
                              + "   quads ON SCREEN " + peakSeen
                              + "   peak alpha " + peakAlpha.ToString("F2")
                              + "   textured " + (textured ? "yes" : "NO"));
                sb.AppendLine("    trail depth " + nearest.ToString("F0") + ".." + furthest.ToString("F0")
                              + " units    screen y " + minY.ToString("F2") + ".." + maxY.ToString("F2")
                              + "   (car y " + carPos.y.ToString("F2")
                              + ", view y " + (-halfH).ToString("F2") + ".." + halfH.ToString("F2") + ")");
                sb.AppendLine("    cut: strip-break " + r.skid.LastCutByStripBreak
                              + "  too near " + r.skid.LastCutByNear
                              + "  too far " + r.skid.LastCutByFar
                              + "  unprojectable " + r.skid.LastCutByProjection);
                Func<bool, string> mark = gating ? (Func<bool, string>)Pass : Read;
                sb.AppendLine("    stored=" + mark(marksStored) + "  built=" + mark(quadsBuilt)
                              + "  on screen=" + mark(onScreen) + "  opaque(>=0.20)=" + mark(opaque)
                              + "  reaches the car=" + mark(behindCar)
                              + "   " + mark(ok));
                sb.AppendLine();
                return ok;
            }
            finally { r.Destroy(); }
        }
    }
}
