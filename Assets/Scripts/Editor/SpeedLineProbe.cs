using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.UI;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Are the high-speed streaks actually ON THE SCREEN at racing speed, and are they at
    /// the EDGES of it? Editor-only, never ships.
    ///
    /// WHY THIS EXISTS. Checklist 10b.7 was left open with the honest reason: the
    /// headless browser has no GPU and runs the game at roughly a fifteenth of real time,
    /// so the car cannot be held on a fast enough line long enough to see the effect, and
    /// the change was "constants only". That is exactly the position the skid marks were
    /// in for three phases - and exactly the thing SkidMarkProbe fixed by driving the
    /// real code and measuring the real output instead of reasoning about it.
    ///
    /// So this builds the REAL <see cref="SpeedLines"/> on a real canvas at the authored
    /// 1280x720, ticks it at a chosen fraction of top speed, and then asks the three
    /// questions the player is asking:
    ///
    ///   1. CAN THEY BE SEEN?    how many streaks clear a visibility floor, and how
    ///                           opaque the strongest one gets.
    ///   2. ARE THEY AT THE EDGE? the inboard-most pixel any streak reaches. The brief is
    ///                           marks at the left and right edges and nothing over the
    ///                           car, so anything inside the middle 40% of the frame is a
    ///                           failure however pretty it looks.
    ///   3. DO THEY STAY OFF at a cruise, so that seeing them still MEANS something.
    ///
    /// The measurements are CPU-side (canvas layout and vertex colours), so they answer in
    /// batch mode with -nographics. A PNG of each speed is written too when there is a
    /// graphics device, because "it is on screen" is finally a thing to be looked at.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///     -executeMethod RenaultRacer.EditorTools.SpeedLineProbe.RunFromCommandLine
    /// </summary>
    public static class SpeedLineProbe
    {
        private const float Width = 1280f, Height = 720f;   // the canvas is authored here
        private const float Step = 1f / 60f;
        private const float Settle = 2.5f;                  // seconds ticked per speed
        private const string LogPath = "Logs/speed-line-probe.txt";

        // HudView.TopSpeedKmh: the HUD reads 220 km/h at speedPct 1.
        private const float TopSpeedKmh = 220f;

        // An alpha a player can actually notice over a bright sky, and the fraction of
        // the frame that belongs to the car and the HUD rather than to the effect.
        private const float VisibleAlpha = 0.08f;
        private const float CentreBand = 0.40f;

        [MenuItem("Renault Racer/Speed line probe (Phase 10c)", false, 47)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e)
            {
                Console.WriteLine("[SpeedLineProbe] threw: " + e);
                EditorApplication.Exit(2);
                return;
            }
            Console.WriteLine(report);
            Directory.CreateDirectory("Logs");
            File.WriteAllText(LogPath, report);
            EditorApplication.Exit(report.Contains("**FAIL**") ? 1 : 0);
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        private static string Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SPEED LINE PROBE - the real SpeedLines on a real canvas, "
                          + Width.ToString("0") + "x" + Height.ToString("0"));
            sb.AppendLine();

            GameObject canvasGo = null;
            GameObject cameraGo = null;
            try
            {
                // A WORLD-SPACE canvas, sized to the authored resolution. Overlay would
                // take the game view's size, which in batch mode is whatever Unity feels
                // like - and the whole point is to measure against the layout the game
                // is authored at.
                canvasGo = new GameObject("ProbeCanvas", typeof(Canvas));
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var cr = (RectTransform)canvasGo.transform;
                cr.sizeDelta = new Vector2(Width, Height);
                cr.position = Vector3.zero;

                var lines = canvasGo.AddComponent<SpeedLines>();
                lines.Build(canvasGo.transform);
                // Before/after captures: RR_SL_MAXALPHA overrides the peak alpha (0.55 was
                // the approved value before the +25% change), RR_SL_BRIGHT=1 uses a pale sky,
                // RR_SL_TAG prefixes the PNG names (written to Logs/speedlines/).
                string envA = Environment.GetEnvironmentVariable("RR_SL_MAXALPHA");
                bool overridden = !string.IsNullOrEmpty(envA);
                if (overridden) lines.maxAlpha = float.Parse(envA, CultureInfo.InvariantCulture);
                string tag = Environment.GetEnvironmentVariable("RR_SL_TAG") ?? "";
                bool bright = Environment.GetEnvironmentVariable("RR_SL_BRIGHT") == "1";

                sb.AppendLine("art: fx_atlas streak "
                              + (Effects.FxArt.Streak != null
                                 ? Effects.FxArt.Streak.rect.width.ToString("0") + "x"
                                   + Effects.FxArt.Streak.rect.height.ToString("0")
                                 : "MISSING - falling back to the plain box"));
                sb.AppendLine("thresholds: threshold " + lines.threshold.ToString("0.00")
                              + "  maxAlpha " + lines.maxAlpha.ToString("0.00")
                              + "  travel " + lines.travel.ToString("0") + " px");
                sb.AppendLine();

                cameraGo = new GameObject("ProbeCamera", typeof(Camera));
                var cam = cameraGo.GetComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Height * 0.5f;
                cam.aspect = Width / Height;
                cam.transform.position = new Vector3(0f, 0f, -100f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                // The sky the streaks are hardest to see against.
                cam.backgroundColor = bright ? new Color(0.86f, 0.93f, 1f) : new Color(0.28f, 0.6f, 0.86f);
                canvas.worldCamera = cam;

                bool allOk = true;
                foreach (float pct in new[] { 0.30f, 0.60f, 0.75f, 0.91f, 1.00f })
                {
                    // Tick from a standstill each time, so nothing carries over from the
                    // speed measured before this one.
                    lines.ResetDisplay();
                    var s = new Sample();
                    for (float t = 0f; t < Settle; t += Step)
                    {
                        lines.Tick(pct, Step);
                        s.Merge(Measure(lines, canvasGo));
                    }

                    int kmh = Mathf.RoundToInt(pct * TopSpeedKmh);
                    bool ok;
                    string verdict;
                    if (pct <= lines.threshold)
                    {
                        ok = s.peakAlpha <= 0.001f;
                        verdict = ok ? "silent, as it should be below the threshold"
                                     : "DRAWN BELOW THE THRESHOLD";
                    }
                    else
                    {
                        // Just over the threshold the effect is MEANT to be barely there,
                        // so "visible" is only required once there is real pace on.
                        bool seen = pct < 0.7f
                                  ? s.visibleStreaks >= 1
                                  : s.visibleStreaks >= 4 && s.peakAlpha >= 0.20f;
                        bool edges = s.deepestFraction <= (1f - CentreBand) * 0.5f;
                        ok = seen && edges;
                        verdict = (seen ? "visible" : "TOO FAINT TO SEE")
                                + ", " + (edges ? "at the edges" : "REACHES OVER THE CAR");
                    }
                    // The +25% change (0.55 -> 0.69): hold the strength bands. Skipped when
                    // the alpha is overridden for a before/after capture.
                    if (!overridden)
                    {
                        string band = null;
                        if (pct <= 0.31f) { if (s.peakAlpha > 0.001f) band = "must be absent"; }
                        else if (pct <= 0.61f) { if (s.peakAlpha < 0.05f || s.peakAlpha > 0.20f) band = "medium must be subtle (0.05..0.20)"; }
                        else if (pct >= 0.99f) { if (s.peakAlpha < 0.65f || s.peakAlpha > 0.70f) band = "top must be ~0.69 (0.65..0.70, +25% over 0.55)"; }
                        if (band != null) { ok = false; verdict += "  ** " + band; }
                    }
                    allOk &= ok;

                    sb.AppendLine(Pass(ok) + "  " + (pct * 100f).ToString("0") + "% of top speed ("
                                  + kmh + " km/h)");
                    sb.AppendLine("        streaks over alpha " + VisibleAlpha.ToString("0.00")
                                  + ": " + s.visibleStreaks + " of " + s.total
                                  + "   peak alpha " + s.peakAlpha.ToString("0.000"));
                    sb.AppendLine("        longest streak " + s.longest.ToString("0")
                                  + " px   total lit width " + s.litWidth.ToString("0") + " px");
                    sb.AppendLine("        deepest lit pixel " + (s.deepestFraction * 100f).ToString("0.0")
                                  + "% in from the frame edge (must stay outside the middle "
                                  + (CentreBand * 100f).ToString("0") + "%, i.e. under "
                                  + ((1f - CentreBand) * 50f).ToString("0") + "%)");
                    sb.AppendLine("        " + verdict);
                    sb.AppendLine();

                    Capture(cam, "speedlines/" + tag + "speedlines-" + (pct * 100f).ToString("000", CultureInfo.InvariantCulture));
                }

                sb.AppendLine(allOk
                    ? "ALL PASS - the effect is on the screen, at the edges, and off at a cruise."
                    : "**FAIL** - see the lines above.");
                return sb.ToString();
            }
            finally
            {
                if (canvasGo != null) UnityEngine.Object.DestroyImmediate(canvasGo);
                if (cameraGo != null) UnityEngine.Object.DestroyImmediate(cameraGo);
            }
        }

        private struct Sample
        {
            public int total, visibleStreaks;
            public float peakAlpha, longest, litWidth, deepestFraction;

            public void Merge(Sample o)
            {
                total = Mathf.Max(total, o.total);
                visibleStreaks = Mathf.Max(visibleStreaks, o.visibleStreaks);
                peakAlpha = Mathf.Max(peakAlpha, o.peakAlpha);
                longest = Mathf.Max(longest, o.longest);
                litWidth = Mathf.Max(litWidth, o.litWidth);
                // The DEEPEST any lit streak ever reached toward the middle of the frame.
                // The worst case is the maximum, not the minimum: a streak that has flown
                // right out to the edge is the effect working, not failing.
                deepestFraction = Mathf.Max(deepestFraction, o.deepestFraction);
            }
        }

        private static readonly Vector3[] _corners = new Vector3[4];

        /// <summary>
        /// One frame's worth of measurement, taken off the canvas after layout: the
        /// streaks' own vertex colours and their world corners, which in this canvas are
        /// in authored pixels.
        /// </summary>
        private static Sample Measure(SpeedLines lines, GameObject canvasGo)
        {
            Canvas.ForceUpdateCanvases();
            var s = new Sample();

            Image[] images = canvasGo.GetComponentsInChildren<Image>(true);
            float halfW = Width * 0.5f;
            foreach (Image img in images)
            {
                s.total++;
                float a = img.color.a;
                s.peakAlpha = Mathf.Max(s.peakAlpha, a);
                if (a < VisibleAlpha) continue;
                s.visibleStreaks++;

                img.rectTransform.GetWorldCorners(_corners);
                float minX = float.MaxValue, maxX = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    minX = Mathf.Min(minX, _corners[i].x);
                    maxX = Mathf.Max(maxX, _corners[i].x);
                }
                s.longest = Mathf.Max(s.longest, maxX - minX);
                // Clamped to the frame: a streak that has flown off the edge contributes
                // only the part still on screen.
                float onMin = Mathf.Max(minX, -halfW), onMax = Mathf.Min(maxX, halfW);
                if (onMax > onMin) s.litWidth += onMax - onMin;

                // How far in from ITS OWN edge the streak reaches, as a fraction of the
                // frame width. The streak lives on the side its pivot is anchored to, so
                // the inboard end is the max on the left and the min on the right.
                bool left = (minX + maxX) * 0.5f < 0f;
                float inboard = left ? maxX + halfW : halfW - minX;
                s.deepestFraction = Mathf.Max(s.deepestFraction,
                                              Mathf.Clamp01(inboard / Width));
            }
            return s;
        }

        /// <summary>Render the canvas to Logs/. Needs a real graphics device, so it is
        /// skipped under -nographics rather than failing the probe.</summary>
        private static void Capture(Camera cam, string name)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return;
            Directory.CreateDirectory("Logs");
            int w = (int)Width, h = (int)Height;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                string path = "Logs/" + name + ".png";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }
    }
}
