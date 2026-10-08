using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Effects;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// CAN THE PLAYER SEE THE LIGHTNING BEFORE THEY HIT IT? BoostProbe proves a pickup is
    /// taken and boosts the car; it says nothing about whether it is ever SEEN, and the
    /// first version passed it while being invisible in the browser until it triggered.
    ///
    /// This builds the real RoadRenderer and BoostPickups on the production circuit, puts
    /// the camera at a run of distances behind each pickup, and reads what was actually
    /// placed: the renderer enabled, opaque, on screen, how many pixels tall on the
    /// 720-px virtual screen, and sorted above every prop and the tyre wall. A pickup is
    /// READABLE when all of that holds and it is at least MinReadablePx tall. Graded in
    /// seconds of warning at top speed, measured to where the car is DRAWN.
    ///
    /// With -Graphics it also renders frames to Logs/pickup-visibility-*.png.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.PickupVisibilityProbe.RunFromCommandLine
    /// </summary>
    public static class PickupVisibilityProbe
    {
        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";
        private const string LogPath = "Logs/pickup-visibility-probe.txt";
        private const float DistanceAhead = 1700f;     // CarView.distanceAhead
        private const float TopSpeed = 12000f;         // Production maxSpeed, units/s
        // 30 -> 20 px with the smaller distance curve (2026-10-07): the bolt's body is
        // ~16 px there, and the floor reaches it ~1.65 s out at top speed.
        // 20 -> 16 px with the third-pass curve (2026-10-07, first seen ~10 px): 16 px on
        // the 720-px screen is 24 px at 1080p, the bolt's body ~13 px (19 at 1080p) - three
        // times the 6-8 px floor a distinct shape needs - and the floor reaches it ~1.65 s
        // out at top speed, so the 1.5 s warning still holds.
        private const float MinReadablePx = 16f;
        private const float RequiredSeconds = 1.5f;
        private const int Width = 1280, Height = 720;

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e) { Console.WriteLine("pickup visibility probe threw: " + e); EditorApplication.Exit(2); return; }
            Console.WriteLine(report);
            try { Directory.CreateDirectory("Logs"); File.WriteAllText(LogPath, report); }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }
            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine(clean ? "[pickup visibility probe] ALL PASS" : "[pickup visibility probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        [MenuItem("Renault Racer/Pickup visibility probe - can the lightning be seen", false, 48)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static string Run()
        {
            TrackModel track = TrackBuilder.BuildMainTrack();
            var sb = new StringBuilder(4096);
            sb.AppendLine("================ PICKUP VISIBILITY ================");
            sb.AppendLine("readable = enabled, alpha >= 0.9, on screen, >= " + MinReadablePx
                          + " px tall (720-px screen), sorted above props and wall; seconds at top speed to the drawn car");

            var camGo = new GameObject("ProbeCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5f; cam.aspect = 16f / 9f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.nearClipPlane = 0.3f; cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.33f, 0.47f, 0.55f);
            var roadGo = new GameObject("ProbeRoad", typeof(MeshFilter), typeof(MeshRenderer));
            var road = roadGo.AddComponent<RoadRenderer>();
            var pickGo = new GameObject("ProbePickups");
            var pickups = pickGo.AddComponent<BoostPickups>();
            bool ok = true;
            bool gpu = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            RenderTexture rt = gpu ? new RenderTexture(Width, Height, 24) : null;
            try
            {
                road.Initialise(track, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));
                pickups.Initialise(road, track);
                // The size, sorting and asphalt checks below judge the pickup at its collectible
                // position; the approach lift is judged on its own in [approach].
                float liftShipped = pickups.approachLift;
                pickups.approachLift = 0f;
                float ppu = road.virtualHeight / (2f * cam.orthographicSize);
                float viewHalfW = cam.orthographicSize * cam.aspect, viewHalfH = cam.orthographicSize;

                // THE FIRST VERSION, for the record: the same pickups at the old look
                // (1.1 m, no minimum size, 120-segment draw distance). Sorting is not
                // judged here - this row asks only whether it was ever big enough to read.
                {
                    float h = pickups.heightMetres, m = pickups.nearSizePx; int dd = pickups.drawDistance, fs = pickups.fadeSegments;
                    pickups.heightMetres = 1.1f; pickups.nearSizePx = 0f; pickups.drawDistance = 120; pickups.fadeSegments = 20;
                    string row = "  OLD LOOK readable from:";
                    for (int i = 0; i < pickups.Set.Count; i++)
                    {
                        float first = -1f;
                        for (float tAhead = 3f; tAhead >= 0.15f && first < 0f; tAhead -= 1f / 60f)
                        {
                            road.CameraZ = pickups.Set.ZOf(i) - DistanceAhead - tAhead * TopSpeed;
                            road.CameraX = 0f;
                            road.Rebuild(16f / 9f);
                            pickups.Refresh();
                            SpriteRenderer sr = pickups.RendererOf(i);
                            if (sr.enabled && sr.color.a >= 0.9f && sr.bounds.size.y * ppu >= MinReadablePx) first = tAhead;
                        }
                        row += "  #" + i + " " + (first >= 0f ? first.ToString("F2") + " s" : "never");
                    }
                    sb.AppendLine(row + "   (sorted 10-90: under the tyre wall and the car)");
                    pickups.heightMetres = h; pickups.nearSizePx = m; pickups.drawDistance = dd; pickups.fadeSegments = fs;
                }

                // [scale] needs the true size, not the cosmetic 2% pulse riding on it.
                float pulseWas = pickups.pulseAmount;
                pickups.pulseAmount = 0f;
                string scaleRows = "", asphaltRows = "";
                bool scaleOk = true, asphaltOk = true;
                float bodyHalfAtCar = float.MaxValue;
                for (int i = 0; i < pickups.Set.Count; i++)
                {
                    float pz = pickups.Set.ZOf(i);
                    float firstReadable = -1f, maxPx = 0f, pxAt1s = -1f;
                    float firstPx = -1f, firstAt = -1f, prevPx = -1f, worstDrop = 0f, worstStep = 0f;
                    float px15 = -1f, px10 = -1f, px05 = -1f, nearPx = -1f;
                    bool gap = false;
                    string notReadable = "";
                    // ON THE ASPHALT: the bolt's opaque body (and, read only, its glow) as
                    // far out as it reaches, road half-widths from the centre, every frame.
                    float worstBody = 0f, worstGlow = 0f, bodyHalfNear = -1f, worstFootRow = 0f, worstFootAt = -1f;
                    float drawnLatNear = float.NaN;   // the bolt's drawn lateral at the last (nearest) frame, road half-widths
                    // From 3 s out to 0.15 s out, in 1/60 s steps of travel.
                    for (float tAhead = 3f; tAhead >= 0.15f; tAhead -= 1f / 60f)
                    {
                        float camZ = pz - DistanceAhead - tAhead * TopSpeed;
                        road.CameraZ = camZ;
                        road.CameraX = 0f;
                        road.Rebuild(16f / 9f);
                        pickups.Refresh();
                        SpriteRenderer sr = pickups.RendererOf(i);
                        Bounds b = sr.bounds;
                        float px = b.size.y * ppu;
                        if (sr.enabled)
                        {
                            if (firstPx < 0f) { firstPx = px; firstAt = tAhead; }
                            if (prevPx >= 0f)
                            {
                                worstDrop = Mathf.Max(worstDrop, prevPx - px);
                                worstStep = Mathf.Max(worstStep, px - prevPx);
                            }
                            prevPx = px;
                            if (px15 < 0f && tAhead <= 1.5f) px15 = px;
                            if (px10 < 0f && tAhead <= 1.0f) px10 = px;
                            if (px05 < 0f && tAhead <= 0.5f) px05 = px;
                            nearPx = px;
                            RoadRenderer.SegmentSample smp;
                            if (SampleOf(road, pickups.Set.SegmentOf(i), out smp) && smp.halfWidth > 0f)
                            {
                                float off = Mathf.Abs(b.center.x - smp.worldX);
                                float bodyHalf = 0.5f * b.size.x * PickupArt.BoltBodyWidth / smp.halfWidth;
                                worstBody = Mathf.Max(worstBody, off / smp.halfWidth + bodyHalf);
                                worstGlow = Mathf.Max(worstGlow, (off + 0.5f * b.size.x) / smp.halfWidth);
                                bodyHalfNear = bodyHalf;
                                drawnLatNear = (b.center.x - smp.worldX) / smp.halfWidth;
                            }
                            // What the EYE reads: the road row the bolt's foot (the sprite's
                            // pivot) is drawn on. The body must sit inside that row's edges.
                            float rowX, rowHalf;
                            if (RowAt(road, sr.transform.position.y, out rowX, out rowHalf) && rowHalf > 0f)
                            {
                                float fr = (Mathf.Abs(b.center.x - rowX) + 0.5f * b.size.x * PickupArt.BoltBodyWidth) / rowHalf;
                                if (fr > worstFootRow) { worstFootRow = fr; worstFootAt = tAhead; }
                            }
                        }
                        else prevPx = -1f;    // hidden by a crest: the curve restarts after it
                        bool onScreen = b.max.x > -viewHalfW && b.min.x < viewHalfW
                                     && b.max.y > -viewHalfH && b.min.y < viewHalfH;
                        bool readable = sr.enabled && sr.color.a >= 0.9f && onScreen && px >= MinReadablePx
                                     && sr.sortingOrder > 95 && sr.sortingOrder < 100;
                        if (readable)
                        {
                            if (firstReadable < 0f) firstReadable = tAhead;
                            maxPx = Mathf.Max(maxPx, px);
                            if (pxAt1s < 0f && tAhead <= 1f) pxAt1s = px;
                        }
                        else if (firstReadable >= 0f && tAhead > 0.2f)
                        {
                            gap = true;
                            if (notReadable.Length == 0)
                                notReadable = " lost at " + tAhead.ToString("F2") + " s (enabled " + sr.enabled
                                            + ", a " + sr.color.a.ToString("F2") + ", " + px.ToString("F0") + " px)";
                        }
                    }
                    // THE SCALE CURVE (2026-10-07): small far away, growing smoothly, the
                    // approved size at the car. Not merely "visible" - a flat 52 px floor
                    // passed every visibility check while reading as a HUD icon.
                    // 2026-10-07 (second pass): Maj found 22/33/47/66/90 still too big far
                    // away. Bands are absolute px on the 720-px screen: far 12-19, 1.5 s
                    // 20-28, 1.0 s 28-39, 0.5 s 43-56, at the car 68-80.
                    // THIRD PASS (2026-10-07): far 8-12 (was 12-19: ~60%), 1.5 s 15-22,
                    // 1.0 s 26-35, 0.5 s 43-54, at the car 64-76. Re-derived from the new
                    // curve (9.8 / 18 / 31 / 49 / 70), about +-20% either side of it.
                    // "First seen" is the far size only when it appears AT drawDistance; one
                    // revealed later by a corner or crest (#3, after T14) appears at whatever
                    // the curve is there, which must not exceed the 1.5 s band.
                    float drawSec = (pickups.drawDistance - pickups.carDepthSegments) * RoadGeometry.SegmentLength / TopSpeed;
                    bool atDraw = firstAt >= drawSec - 0.05f;
                    bool sOk = nearPx >= 64f && nearPx <= 76f
                            && (atDraw ? firstPx >= 8f && firstPx <= 12f : firstPx >= 8f && firstPx <= 22f)
                            && px15 >= 15f && px15 <= 22f
                            && px10 >= 26f && px10 <= 35f
                            && px05 >= 43f && px05 <= 54f
                            && worstDrop <= 0.5f && worstStep <= 0.08f * nearPx;
                    if (bodyHalfNear > 0f) bodyHalfAtCar = Mathf.Min(bodyHalfAtCar, bodyHalfNear);
                    // Clear of the kerb too (rumble strip starts at |x| 0.92) and of the tyre wall, which
                    // stands out in the run-off beyond the asphalt edge (BarrierLeft/RightAt).
                    float wallIn = Mathf.Min(-track.BarrierLeftAt(pz), track.BarrierRightAt(pz));
                    bool aOk = worstBody <= 0.92f && worstBody < wallIn && worstFootRow <= 1.0f
                            // final visible x == collision x where it is taken (0.15 s out)
                            && Mathf.Abs(drawnLatNear - pickups.Set.LateralOf(i)) <= 0.01f;
                    if (!aOk) asphaltOk = false;
                    asphaltRows += "  #" + i + " x " + pickups.Set.LateralOf(i).ToString("+0.00;-0.00")
                                 + ": bolt body reaches " + worstBody.ToString("F2") + " of its own segment's half-width (kerb at 0.92, wall at " + wallIn.ToString("F2") + "), "
                                 + worstFootRow.ToString("F2") + " of the road row its foot is drawn on (worst at " + worstFootAt.ToString("F2") + " s; both <= 1.00 "
                                 + (aOk ? "PASS" : "**FAIL**") + "), glow " + worstGlow.ToString("F2")
                                 + " (read), body half-width at the car " + bodyHalfNear.ToString("F3")
                                 + ", drawn x at 0.15 s " + drawnLatNear.ToString("+0.000;-0.000") + " vs collision x " + pickups.Set.LateralOf(i).ToString("+0.000;-0.000")
                                 + System.Environment.NewLine;
                    if (!sOk) scaleOk = false;
                    scaleRows += "  #" + i + " first seen " + firstAt.ToString("F2") + " s at " + firstPx.ToString("F1")
                               + " px (" + (100f * firstPx / Mathf.Max(1f, nearPx)).ToString("F0") + "%"
                               + (atDraw ? ")" : ", revealed late)")
                               + "   1.5 s " + px15.ToString("F0") + "   1.0 s " + px10.ToString("F0")
                               + " (" + (100f * px10 / Mathf.Max(1f, nearPx)).ToString("F0") + "%)"
                               + "   0.5 s " + px05.ToString("F0") + "   near " + nearPx.ToString("F0")
                               + "   worst shrink " + worstDrop.ToString("F1") + "   worst frame step " + worstStep.ToString("F1")
                               + "   " + (sOk ? "PASS" : "**FAIL**") + System.Environment.NewLine;
                    bool pass = firstReadable >= RequiredSeconds && !gap;
                    if (!pass) ok = false;
                    sb.AppendLine("  #" + i + " segment " + pickups.Set.SegmentOf(i) + " x " + pickups.Set.LateralOf(i).ToString("F2")
                                  + ": readable from " + (firstReadable >= 0f ? firstReadable.ToString("F2") + " s" : "NEVER")
                                  + " ahead, " + (pxAt1s >= 0f ? pxAt1s.ToString("F0") : "--") + " px at 1 s (bolt body ~"
                                  + (pxAt1s >= 0f ? (pxAt1s * PickupArt.BoltBodyHeight).ToString("F0") : "--") + "), up to "
                                  + maxPx.ToString("F0") + " px" + notReadable
                                  + "   (>= " + RequiredSeconds + " s, continuous) " + (pass ? "PASS" : "**FAIL**"));
                }

                pickups.pulseAmount = pulseWas;
                if (!scaleOk) ok = false;
                sb.AppendLine("[scale] px on the 720-px screen: first seen 8-12, 1.5 s 15-22, 1.0 s 26-35, 0.5 s 43-54, near 64-76; never shrinks, no frame step > 8%:");
                sb.Append(scaleRows);
                sb.AppendLine("  floor: " + pickups.nearSizePx.ToString("F0") + " px at the car easing to "
                              + (pickups.farScale * 100f).ToString("F0") + "% at " + pickups.drawDistance
                              + " segments, exponent " + pickups.scaleExponent.ToString("F1") + ", bolt "
                              + pickups.heightMetres.ToString("F2") + " m   SCALE CURVE = " + (scaleOk ? "PASS" : "**FAIL**"));
                if (!asphaltOk) ok = false;
                sb.AppendLine("[asphalt] the bolt's opaque body stays on the road (|x| <= 1 half-width) all the way in:");
                sb.Append(asphaltRows);

                ok &= Collection(pickups, track, bodyHalfAtCar, sb);
                ok &= WrapAndLifecycle(road, pickups, track, sb);
                pickups.approachLift = liftShipped;
                ok &= Approach(road, pickups, sb);
                ok &= Drop(road, pickups, sb);

                // THE COLLECTION BURST'S ENVELOPE, as BoostPickups computes it: off before and
                // after, at full strength within 0.02 s, back to exactly zero by the end.
                {
                    float life = pickups.burstSeconds;
                    bool before = BoostPickups.BurstAlpha(-0.01f, life) == 0f;
                    bool rise = BoostPickups.BurstAlpha(0.02f, life) >= 0.999f;
                    bool after = BoostPickups.BurstAlpha(life, life) == 0f;
                    bool falls = true; float last = 1f;
                    for (float a = 0.02f; a < life; a += 0.005f)
                    { float v = BoostPickups.BurstAlpha(a, life); if (v > last + 1e-5f) falls = false; last = v; }
                    pickups.StartBurstForProbe();
                    pickups.ResetAll();
                    bool resetOff = !pickups.BurstActive;
                    bool art = pickups.BurstRenderer != null;
                    bool burstOk = before && rise && after && falls && resetOff && art;
                    if (!burstOk) ok = false;
                    sb.AppendLine("  burst " + life.ToString("F2") + " s: off before=" + before + "  peak by 0.02 s=" + rise
                                  + "  only falls after=" + falls + "  zero at the end=" + after
                                  + "  reset hides it=" + resetOff + "  arc art loaded=" + art
                                  + "   " + (burstOk ? "PASS" : "**FAIL**"));
                }

                if (rt != null)
                {
                    cam.targetTexture = rt;
                    pickups.Set.ResetAll();
                    // Pickup #0 (a side lane) from far to near, then the run to the flag
                    // 0.6 s before the line: nothing of the next lap may be in that frame.
                    float[] shots = { 2.0f, 1.5f, 1.0f, 0.5f, 0.2f, -1f };
                    foreach (float tAhead in shots)
                    {
                        Frame(road, pickups, tAhead >= 0f ? pickups.Set.ZOf(0) - DistanceAhead - tAhead * TopSpeed
                                                          : track.Length - DistanceAhead - 0.6f * TopSpeed);
                        cam.Render();
                        RenderTexture.active = rt;
                        var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                        tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                        tex.Apply();
                        RenderTexture.active = null;
                        string path = tAhead >= 0f ? "Logs/pickup-visibility-" + tAhead.ToString("F1").Replace(',', '.') + "s.png"
                                                   : "Logs/pickup-visibility-finish-approach.png";
                        File.WriteAllBytes(path, tex.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(tex);
                        sb.AppendLine("  rendered " + path);
                    }
                    cam.targetTexture = null;
                    ApproachStrips(road, pickups, cam, sb);
                }
                else sb.AppendLine("  (no GPU: frames not rendered - run with graphics for the PNGs)");
            }
            finally
            {
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(pickGo);
                UnityEngine.Object.DestroyImmediate(roadGo);
                UnityEngine.Object.DestroyImmediate(camGo);
            }
            sb.AppendLine("PICKUP VISIBILITY VERDICT = " + (ok ? "PASS" : "**FAIL**"));
            return sb.ToString();
        }

        /// <summary>The car's half-width in road half-widths (CarView.widthFraction 0.70 / 2).</summary>
        private const float CarHalfWidth = 0.35f;

        private static void Frame(RoadRenderer road, BoostPickups pickups, float camZ)
        {
            road.CameraZ = camZ;
            road.CameraX = 0f;
            road.Rebuild(16f / 9f);
            pickups.Refresh();
        }

        /// <summary>The nearest drawn sample of track segment <paramref name="seg"/>.</summary>
        private static bool SampleOf(RoadRenderer road, int seg, out RoadRenderer.SegmentSample s)
        {
            int n = road.SampleCount;
            for (int k = 0; k < n; k++)
            {
                s = road.GetSample(k);
                if (s.index == seg) return true;
            }
            s = default(RoadRenderer.SegmentSample);
            return false;
        }

        /// <summary>
        /// The road's centre and half-width, world units, on the screen row
        /// <paramref name="y"/>: interpolated between the drawn samples either side of it.
        /// </summary>
        private static bool RowAt(RoadRenderer road, float y, out float x, out float half)
        {
            x = 0f; half = 0f;
            int n = road.SampleCount;
            bool havePrev = false;
            RoadRenderer.SegmentSample prev = default(RoadRenderer.SegmentSample);
            for (int k = 0; k < n; k++)
            {
                RoadRenderer.SegmentSample s = road.GetSample(k);
                if (s.hidden) continue;
                if (s.worldY >= y)
                {
                    if (!havePrev) { x = s.worldX; half = s.halfWidth; return true; }
                    float t = Mathf.Clamp01((y - prev.worldY) / Mathf.Max(1e-5f, s.worldY - prev.worldY));
                    x = Mathf.Lerp(prev.worldX, s.worldX, t);
                    half = Mathf.Lerp(prev.halfWidth, s.halfWidth, t);
                    return true;
                }
                prev = s; havePrev = true;
            }
            return false;
        }

        private static int Enabled(BoostPickups pickups)
        {
            int n = 0;
            for (int i = 0; i < pickups.Set.Count; i++) if (pickups.RendererOf(i).enabled) n++;
            return n;
        }

        /// <summary>
        /// [lanes] [collect] THE SIDE PICKUPS HAVE TO BE GONE FOR. Lanes at +/-0.77 (2026-10-08, was 0.75);
        /// a car holding the centre line takes no side pickup; a car steered onto it does;
        /// and the radius is no wider than what LOOKS like contact (car half-width + the
        /// bolt body's half-width as drawn at the car). Swept with the game's own rule.
        /// </summary>
        private static bool Collection(BoostPickups pickups, TrackModel track, float bodyHalfAtCar, StringBuilder sb)
        {
            BoostPickupSet set = pickups.Set;
            float r = set.Radius;
            bool lanesOk = true;
            string lanes = "";
            for (int i = 0; i < set.Count; i++)
            {
                float a = Mathf.Abs(set.LateralOf(i));
                bool ok = a < 0.01f || Mathf.Abs(a - 0.77f) < 0.005f;
                lanesOk &= ok;
                lanes += "  #" + i + " " + set.LateralOf(i).ToString("+0.00;-0.00") + (ok ? "" : " **FAIL**");
            }

            // A centre-line run of the whole lap, one segment per sweep.
            set.ResetAll();
            int sideTaken = 0, centreTaken = 0, centres = 0;
            for (int i = 0; i < set.Count; i++) if (Mathf.Abs(set.LateralOf(i)) < 0.01f) centres++;
            for (float z = -DistanceAhead; z < track.Length; z += RoadGeometry.SegmentLength)
            {
                int k = set.Sweep(z, z + RoadGeometry.SegmentLength, 0f, 0f);
                if (k < 0) continue;
                if (Mathf.Abs(set.LateralOf(k)) < 0.01f) centreTaken++; else sideTaken++;
            }
            bool centreOk = sideTaken == 0 && centreTaken == centres;

            // Steered to each side pickup: a lane change over 30 segments, on the lane for
            // the last 10. And held just short of reach: not taken.
            int steered = 0, sides = 0, shortTaken = 0;
            float laneAbs = 0f;
            for (int i = 0; i < set.Count; i++)
            {
                float lane = set.LateralOf(i);
                if (Mathf.Abs(lane) < 0.01f) continue;
                sides++;
                laneAbs = Mathf.Abs(lane);
                float pz = set.ZOf(i), seg = RoadGeometry.SegmentLength;
                set.ResetAll();
                for (float z = pz - 40f * seg; z < pz + 5f * seg; z += seg)
                {
                    float x0 = lane * Mathf.Clamp01((z - (pz - 40f * seg)) / (30f * seg));
                    float x1 = lane * Mathf.Clamp01((z + seg - (pz - 40f * seg)) / (30f * seg));
                    if (set.Sweep(z, z + seg, x0, x1) == i) steered++;
                }
                set.ResetAll();
                float shortX = Mathf.Sign(lane) * (Mathf.Abs(lane) - r - 0.03f);
                if (set.Sweep(pz - seg, pz + seg, shortX, shortX) >= 0) shortTaken++;
            }
            set.ResetAll();
            bool steerOk = steered == sides && shortTaken == 0;

            float contact = CarHalfWidth + bodyHalfAtCar;
            bool radiusOk = r <= contact + 0.02f;
            // A car driving the centre line (x 0) clears every side bolt by the collection
            // half-width AND by what is drawn (car half-width + bolt body); a car whose centre
            // is just inside reach is still on asphalt (kerb at 0.92) and takes it.
            bool centreClear = laneAbs - r >= 0.25f && laneAbs - contact > 0f;
            int reachTaken = 0;
            for (int i = 0; i < set.Count; i++)
            {
                float lane = set.LateralOf(i);
                if (Mathf.Abs(lane) < 0.01f) continue;
                float pz = set.ZOf(i);
                float rx = Mathf.Sign(lane) * (Mathf.Abs(lane) - r + 0.02f);
                set.ResetAll();
                if (rx * Mathf.Sign(lane) < 0.92f && set.Sweep(pz - 10f, pz + 10f, rx, rx) == i) reachTaken++;
            }
            set.ResetAll();
            bool reachOk = reachTaken == sides;
            bool all = lanesOk && centreOk && steerOk && radiusOk && centreClear && reachOk;
            sb.AppendLine("[lanes] side pickups at |x| 0.77, centre 0:" + lanes + "   " + (lanesOk ? "PASS" : "**FAIL**"));
            sb.AppendLine("[collect] radius " + r.ToString("F2") + " (visual contact at the car " + CarHalfWidth.ToString("F2")
                          + " + bolt body " + bodyHalfAtCar.ToString("F3") + " = " + contact.ToString("F3") + "; radius <= that + 0.02 "
                          + (radiusOk ? "PASS" : "**FAIL**") + ")");
            sb.AppendLine("    centre-line lap: side pickups taken " + sideTaken + ", centre " + centreTaken + "/" + centres
                          + "   " + (centreOk ? "PASS" : "**FAIL**"));
            sb.AppendLine("    steered onto each side pickup: taken " + steered + "/" + sides
                          + "; held 0.03 short of reach (|x| = lane - radius - 0.03): taken " + shortTaken
                          + "   " + (steerOk ? "PASS" : "**FAIL**"));
            sb.AppendLine("    a side pickup needs the car centre " + (laneAbs - r).ToString("F2") + "+ half-widths off the centre line (lane - radius)");
            sb.AppendLine("    centre line clears it: lane - radius " + (laneAbs - r).ToString("F2") + " >= 0.25, lane - (car + bolt body) " + (laneAbs - contact).ToString("F2")
                          + " > 0 " + (centreClear ? "PASS" : "**FAIL**") + "; car centre at lane - radius + 0.02 (on asphalt, < 0.92) takes it: " + reachTaken + "/" + sides
                          + " " + (reachOk ? "PASS" : "**FAIL**"));
            return all;
        }

        /// <summary>
        /// [wrap] [lifecycle] NOTHING OF THE NEXT LAP, NOTHING ONCE TAKEN. The road draws
        /// over the lap boundary and the pickups ride its samples - so the run to the flag
        /// showed lap 2's start-area pickup ahead, in a one-lap race. Then: a taken pickup
        /// is gone from every frame for the rest of the race (wrap copies included), there
        /// is one renderer per pickup and never more drawn than are left, and a restart
        /// brings them all back.
        /// </summary>
        // ------------------------------------------------------------------ approach lift

        /// <summary>
        /// [approach] THE LIGHTNING VERSUS THE CAR'S SILHOUETTE on a phone-sized landscape
        /// view. A real CarView is placed with the car directly in the pickup's line (the
        /// worst case: the car sits on the pickup's lane), and for each time-to-collection the
        /// pickup's opaque body is compared with the car's rect (width DrawnWidth, height
        /// CarHeightPx at the straight view's scale; the car is sorted over the pickup, so
        /// that is what hides it). Everything is world units * (viewport height / 10), because
        /// the camera is orthographic 5: 667x375 and 844x390 differ only in that scale.
        /// BEFORE = approachLift 0 (the old behaviour); AFTER = the shipped value.
        /// </summary>
        private static readonly int[][] Phones = { new[] { 667, 375 }, new[] { 844, 390 } };
        private static readonly float[] ApproachTimes = { 2.0f, 1.5f, 1.0f, 0.7f, 0.5f, 0.35f, 0.2f, 0.1f, 0.0f };

        private struct CarRect { public float x0, x1, y0, y1; }

        private static bool BuildCar(RoadRenderer road, out GameObject go, out CarView view, out SpriteRenderer sr)
        {
            go = new GameObject("ProbeCar");
            sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 100;
            view = go.AddComponent<CarView>();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(CarView).GetField("roadRenderer", flags).SetValue(view, road);
            typeof(CarView).GetField("spriteRenderer", flags).SetValue(view, sr);
            typeof(CarView).GetMethod("Awake", flags).Invoke(view, null);
            return view.HasDirectionalArt;
        }

        private static CarRect CarRectOf(CarView view, CarArt art)
        {
            Vector3 c = view.RearContact;
            float w = view.DrawnWidth;
            float wpp = w / Mathf.Max(1f, art.StraightWidthPx);
            return new CarRect { x0 = c.x - 0.5f * w, x1 = c.x + 0.5f * w, y0 = c.y, y1 = c.y + art.CarHeightPx * wpp };
        }

        /// <summary>Fraction of the bolt body (x and y) covered by the car's rect, 0..1.</summary>
        private static float Hidden(SpriteRenderer p, CarRect car, out float topAbove, out float bottomAbove)
        {
            Bounds b = p.bounds;
            float foot = p.transform.position.y;
            float bodyH = b.size.y * PickupArt.BoltBodyHeight;
            float x0 = b.center.x - 0.5f * b.size.x * PickupArt.BoltBodyWidth;
            float x1 = b.center.x + 0.5f * b.size.x * PickupArt.BoltBodyWidth;
            topAbove = foot + bodyH - car.y1;
            bottomAbove = foot - car.y1;
            float ox = Mathf.Max(0f, Mathf.Min(x1, car.x1) - Mathf.Max(x0, car.x0)) / Mathf.Max(1e-5f, x1 - x0);
            float oy = Mathf.Max(0f, Mathf.Min(foot + bodyH, car.y1) - Mathf.Max(foot, car.y0)) / Mathf.Max(1e-5f, bodyH);
            return ox * oy;
        }

        private static void PlaceFrame(RoadRenderer road, BoostPickups pickups, CarView view, float z, float lat)
        {
            road.CameraZ = z;
            road.CameraX = lat;
            road.Rebuild(Asp);
            view.UpdateView(lat, 0f, 0f, 0f, false, 1f / 60f);
            pickups.CarSpeed = CurSpeed;
            pickups.Refresh();
        }

        private static float CurSpeed = TopSpeed;   // the car speed the frames are laid out for (drives the landing timing)

        /// <summary>Hand the probe's drawn car to the pickups, as the scene does, so the clearance lift sees the car's roof.</summary>
        private static void WireCar(BoostPickups pickups, CarView view)
        {
            typeof(BoostPickups).GetField("carView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(pickups, view);
        }

        private static float Asp = 16f / 9f;   // the viewport aspect the [approach] frames are laid out for

        private static bool Approach(RoadRenderer road, BoostPickups pickups, StringBuilder sb)
        {
            bool all = true;
            Camera cam = Camera.main;
            float aspWas = cam != null ? cam.aspect : 16f / 9f;
            try
            {
                foreach (float a in new[] { 16f / 9f, 844f / 390f })
                {
                    Asp = a;
                    if (cam != null) cam.aspect = a;
                    sb.AppendLine("--- viewport aspect " + a.ToString("F3") + (a > 2f ? " (phone landscape)" : " (desktop 16:9)") + " ---");
                    all &= ApproachAt(road, pickups, sb, a > 2f);
                }
            }
            finally { Asp = 16f / 9f; if (cam != null) cam.aspect = aspWas; }
            return all;
        }

        private static bool ApproachAt(RoadRenderer road, BoostPickups pickups, StringBuilder sb, bool phone)
        {
            sb.AppendLine("[approach] bolt body vs the car's silhouette, car directly in line (px = world * viewport height / 10):");
            GameObject carGo; CarView view; SpriteRenderer carSr;
            if (!BuildCar(road, out carGo, out view, out carSr))
            {
                UnityEngine.Object.DestroyImmediate(carGo);
                sb.AppendLine("  (no car art loaded: skipped)");
                return true;
            }
            WireCar(pickups, view);
            CarArt art = CarArt.Load();
            bool ok = true;
            float after = pickups.approachLift;
            float pulseWas = pickups.pulseAmount;
            pickups.pulseAmount = 0f;
            float k375 = 375f / 10f, k390 = 390f / 10f;
            try
            {
                for (int i = 0; i < pickups.Set.Count; i++)
                {
                    float pz = pickups.Set.ZOf(i), lat = pickups.Set.LateralOf(i);
                    float[] firstHidden = { -1f, -1f };   // seconds to collection at which >= 25% of the body is covered
                    float[] hiddenSec = { 0f, 0f };
                    float[] worstFoot = { 0f, 0f };
                    float[] winMax = { 0f, 0f };          // worst % of the body covered from 1.5 s to 0.35 s out
                    float aboveTop = 0f; float worstFootT = -1f;                  // how far the lifted foot ever rises above the highest visible road row
                    for (int pass = 0; pass < 2; pass++)
                    {
                        pickups.approachLift = pass == 0 ? 0f : after;
                        for (float t = 2.5f; t >= -0.0001f; t -= 1f / 60f)
                        {
                            PlaceFrame(road, pickups, view, pz - DistanceAhead - Mathf.Max(0f, t) * TopSpeed, lat);
                            SpriteRenderer sr = pickups.RendererOf(i);
                            if (!sr.enabled) continue;
                            float top, bot;
                            float hid = Hidden(sr, CarRectOf(view, art), out top, out bot);
                            {
                                float rx, rh;
                                if (RowAt(road, sr.transform.position.y, out rx, out rh) && rh > 0f)
                                {
                                    float fr = (Mathf.Abs(sr.bounds.center.x - rx) + 0.5f * sr.bounds.size.x * PickupArt.BoltBodyWidth) / rh;
                                    if (pass == 1 && fr > worstFoot[1]) worstFootT = t;
                                    worstFoot[pass] = Mathf.Max(worstFoot[pass], fr);
                                }
                            }
                            if (t <= 2.0f && t >= 0.35f) winMax[pass] = Mathf.Max(winMax[pass], hid);
                            if (pass == 1)
                            {
                                float topRow = float.NegativeInfinity;
                                for (int q = 0; q < road.SampleCount; q++) { var sm = road.GetSample(q); if (!sm.hidden && sm.worldY > topRow) topRow = sm.worldY; }
                                aboveTop = Mathf.Max(aboveTop, sr.transform.position.y - topRow);
                            }
                            if (hid >= 0.25f)
                            {
                                if (firstHidden[pass] < 0f) firstHidden[pass] = t;
                                hiddenSec[pass] += 1f / 60f;
                            }
                        }
                    }

                    string rowB = "", rowA = "";
                    for (int ti = 0; ti < ApproachTimes.Length; ti++)
                    {
                        float z = pz - DistanceAhead - ApproachTimes[ti] * TopSpeed;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            pickups.approachLift = pass == 0 ? 0f : after;
                            PlaceFrame(road, pickups, view, z, lat);
                            SpriteRenderer sr = pickups.RendererOf(i);
                            string cell;
                            if (!sr.enabled) cell = " crest";
                            else
                            {
                                float top, bot;
                                float hid = Hidden(sr, CarRectOf(view, art), out top, out bot);
                                cell = " " + (top * k375).ToString("+0;-0;0") + "/" + (bot * k375).ToString("+0;-0;0") + " " + (hid * 100f).ToString("F0") + "%";
                            }
                            if (pass == 0) rowB += cell + " |"; else rowA += cell + " |";
                        }
                    }

                    // The lift's own behaviour: foot(after) - foot(before) every 1/60 s.
                    float maxLift = 0f, maxOverCar = 0f, lastLift = -1f, worstStep = 0f, liftAtCar = 0f;
                    int turns = 0, lastSign = 0;
                    for (float t = 2.5f; t >= -0.0001f; t -= 1f / 60f)
                    {
                        float z = pz - DistanceAhead - Mathf.Max(0f, t) * TopSpeed;
                        float[] foot = new float[2];
                        bool shown = true;
                        float carH = 0f;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            pickups.approachLift = pass == 0 ? 0f : after;
                            PlaceFrame(road, pickups, view, z, lat);
                            SpriteRenderer sr = pickups.RendererOf(i);
                            if (!sr.enabled) shown = false; else foot[pass] = sr.transform.position.y;
                            CarRect cr = CarRectOf(view, art);
                            carH = cr.y1 - cr.y0;
                        }
                        if (!shown) { lastLift = -1f; lastSign = 0; continue; }
                        float lift = foot[1] - foot[0];
                        if (lastLift >= 0f)
                        {
                            worstStep = Mathf.Max(worstStep, Mathf.Abs(lift - lastLift) * k375);
                            float dl = lift - lastLift;
                            if (Mathf.Abs(dl) > 0.003f) { int sg = dl > 0f ? 1 : -1; if (lastSign != 0 && sg != lastSign) turns++; lastSign = sg; }
                        }
                        lastLift = lift;
                        maxLift = Mathf.Max(maxLift, lift);
                        maxOverCar = Mathf.Max(maxOverCar, lift / Mathf.Max(1e-5f, carH));
                        if (t <= 0.0001f) liftAtCar = lift;
                    }
                    bool monotone = turns <= 4;   // rises with the bolt's size, then eases to zero: no wobble
                    bool improves = after <= 0f || hiddenSec[1] <= hiddenSec[0] + 0.02f;   // the lift lands a second out: it must never make the cover worse
                    bool atCarOk = Mathf.Abs(liftAtCar * k390) < 0.5f;
                    bool pOk = after <= 0f || (improves && atCarOk && worstStep <= 2.5f && monotone && maxOverCar <= 0.9f && aboveTop <= 0.02f && winMax[1] <= winMax[0] + 0.02f);
                    if (!pOk) ok = false;
                    sb.AppendLine("  #" + i + " lane " + lat.ToString("+0.00;-0.00") + "   cells = body top/bottom above the car roof (px @667x375), % of body covered; t = "
                                  + string.Join(" ", Array.ConvertAll(ApproachTimes, f => f.ToString("F2"))) + " s");
                    sb.AppendLine("      BEFORE:" + rowB);
                    sb.AppendLine("      AFTER :" + rowA);
                    sb.AppendLine("      >=25% covered from " + firstHidden[0].ToString("F2") + " s out (before; " + hiddenSec[0].ToString("F2")
                                  + " s in all) -> " + (firstHidden[1] < 0f ? "never" : firstHidden[1].ToString("F2") + " s out") + " (after; "
                                  + hiddenSec[1].ToString("F2") + " s in all).  Lift max " + (maxLift * k375).ToString("F1") + " px @375 / "
                                  + (maxLift * k390).ToString("F1") + " px @390 = " + (maxOverCar * 100f).ToString("F0") + "% of the car's height; at collection "
                                  + (liftAtCar * k390).ToString("F2") + " px; body vs the road row it is drawn on, read only (worst " + worstFoot[1].ToString("F2") + " at " + worstFootT.ToString("F2") + " s" + " <= max(1.00, " + worstFoot[0].ToString("F2") + " without the lift)); worst step " + worstStep.ToString("F2") + " px/frame; smooth (direction changes " + turns + ")=" + monotone
                                  + "; covered 2.0-0.35 s out: " + (winMax[0] * 100f).ToString("F0") + "% -> " + (winMax[1] * 100f).ToString("F0")
                                  + "% (after must not exceed before); foot above the highest visible road row: " + aboveTop.ToString("F3") + " (<= 0)"
                                  + "   " + (pOk ? "PASS" : "**FAIL**"));
                }
            }
            finally
            {
                pickups.approachLift = after;
                pickups.pulseAmount = pulseWas;
            }
            WireCar(pickups, null);
            UnityEngine.Object.DestroyImmediate(carGo);
            return ok;
        }


        /// <summary>
        /// [drop] THE LANDING. Screen x with the lift on must equal screen x with it off on every
        /// frame (the drop is purely vertical); the lift is exactly 0 within 1.0 s of contact
        /// at 30/60/100% speed and inside the distance floor; it never grows as the car closes;
        /// and it never carries the foot above the highest visible road row. Every pickup (flat
        /// runs, a descent, a crest, bends) at the three speeds, every 1/60 s from 4 s out.
        /// </summary>
        private static bool Drop(RoadRenderer road, BoostPickups pickups, StringBuilder sb)
        {
            GameObject carGo; CarView view; SpriteRenderer carSr;
            if (!BuildCar(road, out carGo, out view, out carSr))
            {
                UnityEngine.Object.DestroyImmediate(carGo);
                sb.AppendLine("[drop] (no car art loaded: skipped)");
                return true;
            }
            WireCar(pickups, view);
            Asp = 844f / 390f;
            bool ok = true;
            float after = pickups.approachLift, pulseWas = pickups.pulseAmount, speedWas = CurSpeed;
            pickups.pulseAmount = 0f;
            float seg = RoadGeometry.SegmentLength;
            float floorUnits = pickups.approachFloorSegs * seg;
            sb.AppendLine("[drop] landing: lift 0 at time-to-contact <= " + pickups.approachLandSeconds.ToString("F1") + " s and within " + pickups.approachFloorSegs.ToString("F0")
                          + " segments; full from " + pickups.approachFullSeconds.ToString("F1") + " s; screen x identical with the lift on and off; never grows on approach; foot never above the top road row");
            try
            {
                foreach (float frac in new[] { 0.3f, 0.6f, 1.0f })
                {
                    float speed = TopSpeed * frac;
                    CurSpeed = speed;
                    float maxDx = 0f, maxLiftIn = 0f, maxGrow = 0f, maxAbove = 0f, maxLift = 0f, landDist = 0f;
                    int frames = 0, liftedFrames = 0;
                    for (int i = 0; i < pickups.Set.Count; i++)
                    {
                        float pz = pickups.Set.ZOf(i), lat = pickups.Set.LateralOf(i);
                        float prevLift = float.PositiveInfinity;
                        for (float dist = 4f * speed + 8f * seg; dist >= 0f; dist -= speed / 60f)
                        {
                            float z = pz - DistanceAhead - dist;
                            float x1 = 0f, y1 = 0f, x0 = 0f, y0 = 0f; bool show = true;
                            for (int pass = 0; pass < 2; pass++)
                            {
                                pickups.approachLift = pass == 0 ? 0f : after;
                                PlaceFrame(road, pickups, view, z, lat);
                                SpriteRenderer sr = pickups.RendererOf(i);
                                if (!sr.enabled) { show = false; break; }
                                if (pass == 0) { x0 = sr.transform.position.x; y0 = sr.transform.position.y; }
                                else { x1 = sr.transform.position.x; y1 = sr.transform.position.y; }
                            }
                            if (!show) { prevLift = float.PositiveInfinity; continue; }
                            frames++;
                            float lift = y1 - y0;
                            float topRow = float.NegativeInfinity;
                            for (int q = 0; q < road.SampleCount; q++) { var sm = road.GetSample(q); if (!sm.hidden && sm.worldY > topRow) topRow = sm.worldY; }
                            maxDx = Mathf.Max(maxDx, Mathf.Abs(x1 - x0));
                            maxAbove = Mathf.Max(maxAbove, y1 - topRow);
                            maxLift = Mathf.Max(maxLift, lift);
                            if (lift > 1e-5f) { liftedFrames++; landDist = landDist == 0f ? dist : Mathf.Min(landDist, dist); }
                            if (dist / speed <= pickups.approachLandSeconds + 1e-4f || dist <= floorUnits) maxLiftIn = Mathf.Max(maxLiftIn, lift);
                            if (lift > prevLift) maxGrow = Mathf.Max(maxGrow, lift - prevLift);
                            prevLift = lift;
                        }
                    }
                    pickups.approachLift = after;
                    bool fOk = maxDx <= 1e-4f && maxLiftIn <= 1e-5f && maxGrow <= 0.0134f && maxAbove <= 0.02f;
                    if (!fOk) ok = false;
                    sb.AppendLine("  speed " + (frac * 100f).ToString("F0") + "% (" + speed.ToString("F0") + " u/s): " + frames + " frames, " + liftedFrames + " lifted; max |dx| lift on vs off "
                                  + maxDx.ToString("E1") + " (<= 1e-4); max lift inside ttc 1.0 s / floor " + maxLiftIn.ToString("E1") + " (0); worst growth on approach "
                                  + maxGrow.ToString("F4") + " world (<= 0.0134 = half a pixel at 667x375: the road under it moves); max foot above top road row " + maxAbove.ToString("F3") + " (<= 0.02); biggest lift " + (maxLift * 37.5f).ToString("F1")
                                  + " px@375; landed (last lifted frame) at " + (landDist / seg).ToString("F1") + " segments before the pickup = " + (landDist / speed).ToString("F2") + " s   "
                                  + (fOk ? "PASS" : "**FAIL**"));
                }
                // The distance floor on its own: a stopped car and a crawl.
                foreach (float sp in new[] { 0f, 300f })
                {
                    CurSpeed = sp;
                    float worst = 0f;
                    for (int i = 0; i < pickups.Set.Count; i++)
                    {
                        float pz = pickups.Set.ZOf(i), lat = pickups.Set.LateralOf(i);
                        for (float dist = 0f; dist <= floorUnits; dist += seg * 0.25f)
                        {
                            float z = pz - DistanceAhead - dist;
                            float y0 = 0f, y1 = 0f; bool show = true;
                            for (int pass = 0; pass < 2; pass++)
                            {
                                pickups.approachLift = pass == 0 ? 0f : after;
                                PlaceFrame(road, pickups, view, z, lat);
                                SpriteRenderer sr = pickups.RendererOf(i);
                                if (!sr.enabled) { show = false; break; }
                                if (pass == 0) y0 = sr.transform.position.y; else y1 = sr.transform.position.y;
                            }
                            if (show) worst = Mathf.Max(worst, y1 - y0);
                        }
                    }
                    pickups.approachLift = after;
                    bool sOk = worst <= 1e-5f;
                    if (!sOk) ok = false;
                    sb.AppendLine("  car at " + sp.ToString("F0") + " u/s, bolts within " + pickups.approachFloorSegs.ToString("F0") + " segments: max lift " + worst.ToString("E1") + " (0)   " + (sOk ? "PASS" : "**FAIL**"));
                }
            }
            finally
            {
                pickups.approachLift = after; pickups.pulseAmount = pulseWas; CurSpeed = speedWas; Asp = 16f / 9f;
                WireCar(pickups, null);
                UnityEngine.Object.DestroyImmediate(carGo);
            }
            return ok;
        }

        /// <summary>Before/after strips per phone viewport: columns are times, top row before, bottom row after.</summary>
        private static void ApproachStrips(RoadRenderer road, BoostPickups pickups, Camera cam, StringBuilder sb)
        {
            GameObject carGo; CarView view; SpriteRenderer carSr;
            if (!BuildCar(road, out carGo, out view, out carSr)) { UnityEngine.Object.DestroyImmediate(carGo); return; }
            WireCar(pickups, view);
            pickups.CarSpeed = TopSpeed;
            float after = pickups.approachLift;
            float[] shots = { 2.0f, 1.2f, 0.8f, 0.5f, 0.25f, 0.0f };
            for (int i = 0; i < pickups.Set.Count; i++)
            {
                float lat = pickups.Set.LateralOf(i);
                if (Mathf.Abs(lat) > 0.01f && i != 0 && i != 2) continue;   // the centre pickups, #0 (edge lane) and #2 (a descent)
                foreach (int[] ph in Phones)
                {
                    int w = ph[0], h = ph[1];
                    int cw = Mathf.Min(w, 300);
                    var rtp = new RenderTexture(w, h, 24);
                    float aspWas = cam.aspect;
                    cam.aspect = (float)w / h;
                    cam.targetTexture = rtp;
                    var sheet = new Texture2D(cw * shots.Length, h * 2, TextureFormat.RGB24, false);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        pickups.approachLift = pass == 0 ? 0f : after;
                        for (int s = 0; s < shots.Length; s++)
                        {
                            road.CameraZ = pickups.Set.ZOf(i) - DistanceAhead - shots[s] * TopSpeed;
                            road.CameraX = lat;
                            road.Rebuild(cam.aspect);
                            view.UpdateView(lat, 0f, 0f, 0f, false, 1f / 60f);
                            pickups.Refresh();
                            cam.Render();
                            RenderTexture.active = rtp;
                            var tile = new Texture2D(w, h, TextureFormat.RGB24, false);
                            tile.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                            RenderTexture.active = null;
                            Color[] px = tile.GetPixels((w - cw) / 2, 0, cw, h);
                            sheet.SetPixels(s * cw, (1 - pass) * h, cw, h, px);
                            UnityEngine.Object.DestroyImmediate(tile);
                        }
                    }
                    sheet.Apply();
                    string path = "Logs/pickup-approach-p" + i + "-" + w + "x" + h + ".png";
                    File.WriteAllBytes(path, sheet.EncodeToPNG());
                    sb.AppendLine("  rendered " + path + " (top row before, bottom row after; t = " + string.Join(" ", Array.ConvertAll(shots, f => f.ToString("F2"))) + " s)");
                    UnityEngine.Object.DestroyImmediate(sheet);
                    cam.targetTexture = null;
                    cam.aspect = aspWas;
                    UnityEngine.Object.DestroyImmediate(rtp);
                }
            }
            pickups.approachLift = after;
            WireCar(pickups, null);
            UnityEngine.Object.DestroyImmediate(carGo);
        }

        private static bool WrapAndLifecycle(RoadRenderer road, BoostPickups pickups, TrackModel track, StringBuilder sb)
        {
            BoostPickupSet set = pickups.Set;
            float seg = RoadGeometry.SegmentLength;
            bool ok = true;

            // [wrap] 3 s before the drawn car reaches the line to 2 s after it (coasting).
            set.ResetAll();
            int ghosts = 0; string firstGhost = "";
            for (float tAhead = 3f; tAhead >= -2f; tAhead -= 1f / 60f)
            {
                float camZ = track.Length - DistanceAhead - tAhead * TopSpeed;
                Frame(road, pickups, camZ);
                bool raceOn = camZ + DistanceAhead < track.Length;
                for (int i = 0; i < set.Count; i++)
                {
                    if (!pickups.RendererOf(i).enabled) continue;
                    if (raceOn && set.ZOf(i) > camZ) continue;      // this lap's copy, still ahead
                    ghosts++;
                    if (firstGhost.Length == 0)
                        firstGhost = " (first: #" + i + " segment " + set.SegmentOf(i) + " drawn "
                                   + tAhead.ToString("F2") + " s before the drawn car reaches the line)";
                }
            }
            bool wrapOk = ghosts == 0;
            ok &= wrapOk;
            sb.AppendLine("[wrap] run to the flag and 2 s past it: next-lap pickups drawn in " + ghosts + " frames" + firstGhost
                          + "   " + (wrapOk ? "PASS" : "**FAIL**"));

            // [lifecycle] one at a time: seen, taken, gone in the same frame, never drawn again.
            bool lifeOk = true;
            string rows = "";
            for (int i = 0; i < set.Count; i++)
            {
                set.ResetAll();
                float pz = set.ZOf(i);
                float approach = pz - DistanceAhead - 1.0f * TopSpeed;
                Frame(road, pickups, approach);
                bool seen = pickups.RendererOf(i).enabled;
                bool took = set.Sweep(pz - 1f, pz + 1f, set.LateralOf(i), set.LateralOf(i)) == i;
                Frame(road, pickups, approach);
                bool goneNow = !pickups.RendererOf(i).enabled;
                int leaks = 0;
                for (float camZ = pz - DistanceAhead - 3f * TopSpeed; camZ <= track.Length + 3f * TopSpeed; camZ += seg)
                {
                    Frame(road, pickups, camZ);
                    if (pickups.RendererOf(i).enabled) leaks++;
                }
                bool rowOk = seen && took && goneNow && leaks == 0;
                lifeOk &= rowOk;
                rows += "  #" + i + " seen=" + seen + " taken=" + took + " gone the same frame=" + goneNow
                      + " drawn again in " + leaks + " frames (to 3 s past the flag)" + (rowOk ? "" : " **FAIL**")
                      + System.Environment.NewLine;
            }

            // Everything taken: nothing drawn anywhere. None taken: never more than one at once.
            set.ResetAll();
            for (int i = 0; i < set.Count; i++) set.Sweep(set.ZOf(i) - 1f, set.ZOf(i) + 1f, set.LateralOf(i), set.LateralOf(i));
            int allTaken = set.TakenCount, drawnAfterAll = 0;
            for (float camZ = -DistanceAhead; camZ <= track.Length + 3f * TopSpeed; camZ += seg)
            {
                Frame(road, pickups, camZ);
                drawnAfterAll += Enabled(pickups);
            }
            set.ResetAll();
            int mostAtOnce = 0;
            for (float camZ = -DistanceAhead; camZ <= track.Length; camZ += seg)
            {
                Frame(road, pickups, camZ);
                mostAtOnce = Mathf.Max(mostAtOnce, Enabled(pickups));
            }
            int children = pickups.transform.childCount;
            bool oneEach = children == set.Count + (pickups.BurstRenderer != null ? 1 : 0);
            bool allOk = allTaken == set.Count && drawnAfterAll == 0 && mostAtOnce <= 1 && oneEach;

            // Restart (BoostPickups.ResetAll, as CarController.ResetCar calls it): all back.
            for (int i = 0; i < set.Count; i++) set.Sweep(set.ZOf(i) - 1f, set.ZOf(i) + 1f, set.LateralOf(i), set.LateralOf(i));
            pickups.ResetAll();
            int back = 0;
            for (int i = 0; i < set.Count; i++)
            {
                Frame(road, pickups, set.ZOf(i) - DistanceAhead - 1.0f * TopSpeed);
                if (pickups.RendererOf(i).enabled) back++;
            }
            bool restartOk = back == set.Count && set.TakenCount == 0;

            ok &= lifeOk && allOk && restartOk;
            sb.AppendLine("[lifecycle] semantics: taken once per lap (BoostPickupSet); the race is " + set.Laps
                          + " lap, and no lap past it is drawn or taken - so once taken it is gone for the rest of the race");
            sb.Append(rows);
            sb.AppendLine("    all " + allTaken + " taken: drawn in " + drawnAfterAll + " frames over the lap and 3 s past the flag;"
                          + " most drawn at once with none taken " + mostAtOnce + "; one renderer each (" + children + " children)   "
                          + (allOk ? "PASS" : "**FAIL**"));
            sb.AppendLine("    restart: " + back + "/" + set.Count + " drawn again, TakenCount " + set.TakenCount
                          + "   " + (restartOk ? "PASS" : "**FAIL**"));
            set.ResetAll();
            return ok;
        }
    }
}
