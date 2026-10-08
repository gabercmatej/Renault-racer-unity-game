using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.InputSystem;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Touch hit-region test. Runs the SAME TouchLayout code the game uses at fixed phone
    /// sizes (with notch/rounded-corner safe insets) and asserts: brake inside the safe
    /// area, large enough, clear of the right-steer zone and its chevron by a real gap,
    /// every point of the brake maps to Brake only, steering zones still map to steering,
    /// and brake + steer fingers resolve independently (multitouch).
    ///   -executeMethod RenaultRacer.EditorTools.TouchLayoutProbe.RunFromCommandLine
    /// Report: Logs/touch-layout-probe.txt  (pass marker "[touch layout probe] ALL PASS").
    /// </summary>
    public static class TouchLayoutProbe
    {
        private struct Case
        {
            public string name; public int w, h; public float left, right, top, bottom;
            public Case(string n, int w, int h, float l, float r, float t, float b)
            { name = n; this.w = w; this.h = h; left = l; right = r; top = t; bottom = b; }
            public Rect Safe { get { return new Rect(left, bottom, w - left - right, h - top - bottom); } }
        }

        // Insets in the same px as w/h (CSS px for the phones). y is up, so bottom is yMin.
        public static readonly string[] CaseNames = { "16:9 1280x720", "iPhone 844x390", "iPhone 932x430", "small 667x375", "iPhone 2x 1688x780" };

        private static Case[] Cases()
        {
            return new[]
            {
                new Case("16:9 1280x720", 1280, 720, 0, 0, 0, 0),
                new Case("iPhone 844x390", 844, 390, 47, 47, 0, 21),
                new Case("iPhone 932x430", 932, 430, 47, 47, 0, 21),
                new Case("small 667x375", 667, 375, 0, 0, 0, 0),
                new Case("iPhone 2x 1688x780", 1688, 780, 94, 94, 0, 42),
            };
        }

        public static string RunAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Touch layout probe");
            var d = new TouchDriveInput();
            int fails = 0;
            foreach (Case c in Cases())
            {
                Rect safe = c.Safe;
                Rect b = TouchLayout.BrakeRect(c.w, c.h, safe, true, d.brakeSize, d.brakeMargin, d.brakeInward);
                Rect old = TouchLayout.BrakeRect(c.w, c.h, safe, true, d.brakeSize, d.brakeMargin, 0f);
                float split = c.w * d.steerSplit;
                float scale = Mathf.Sqrt((c.w / 1280f) * (c.h / 720f));   // CanvasScaler match 0.5
                Vector2 lc, rc;
                TouchLayout.ChevronCentres(c.w, c.h, safe, 0.09f, 0.55f, out lc, out rc);
                float half = 112f * scale * 0.5f;
                Rect rChev = new Rect(rc.x - half, rc.y - half, half * 2f, half * 2f);
                float gapSplit = split - b.xMax;
                float gapChev = rChev.xMin - b.xMax;
                float minGap = 0.10f * safe.width;

                sb.AppendLine(string.Format("{0}: brake x {1:F0}-{2:F0} y {3:F0}-{4:F0} (was x {5:F0}); gap to split {6:F0}px ({7:P0} of safe w), to right chevron {8:F0}px",
                    c.name, b.xMin, b.xMax, b.yMin, b.yMax, old.xMin, gapSplit, gapSplit / safe.width, gapChev));

                fails += Check(sb, c.name, "brake inside safe area",
                    b.xMin >= safe.xMin && b.xMax <= safe.xMax && b.yMin >= safe.yMin && b.yMax <= safe.yMax);
                fails += Check(sb, c.name, "brake >= 44px thumb target", b.width >= 44f && b.height >= 44f);
                fails += Check(sb, c.name, "no overlap with right-steer zone (gap >= 10% safe w)", gapSplit >= minGap);
                fails += Check(sb, c.name, "no overlap with right chevron (gap >= 10% safe w)", gapChev >= minGap);
                fails += Check(sb, c.name, "brake inward of old edge placement", b.xMin > old.xMin + 0.01f * c.w);
                fails += Check(sb, c.name, "brake centre in left 40% of screen", b.center.x < 0.40f * c.w);

                // Every point inside the brake -> Brake only.
                bool allBrake = true;
                for (int i = 0; i <= 10; i++)
                    for (int j = 0; j <= 10; j++)
                    {
                        var p = new Vector2(Mathf.Lerp(b.xMin, b.xMax - 0.01f, i / 10f), Mathf.Lerp(b.yMin, b.yMax - 0.01f, j / 10f));
                        if (TouchLayout.Classify(p, b, split) != TouchLayout.Zone.Brake) allBrake = false;
                    }
                fails += Check(sb, c.name, "touch anywhere on brake maps to Brake only", allBrake);

                // Everything right of the split, brake-height included, is steer right, never brake.
                bool rightOk = true;
                for (int i = 0; i <= 10; i++)
                    for (int j = 0; j <= 10; j++)
                    {
                        var p = new Vector2(Mathf.Lerp(split, safe.xMax, i / 10f), Mathf.Lerp(safe.yMin, safe.yMax, j / 10f));
                        if (TouchLayout.Classify(p, b, split) != TouchLayout.Zone.SteerRight) rightOk = false;
                    }
                fails += Check(sb, c.name, "whole right half maps to SteerRight", rightOk);

                // Left half outside the brake still steers left (hit areas unchanged).
                var leftPt = new Vector2(Mathf.Max(safe.xMin, b.xMin - 4f), b.center.y);
                var abovePt = new Vector2(b.center.x, Mathf.Min(safe.yMax, b.yMax + 4f));
                fails += Check(sb, c.name, "left of / above brake still SteerLeft",
                    TouchLayout.Classify(leftPt, b, split) == TouchLayout.Zone.SteerLeft
                    && TouchLayout.Classify(abovePt, b, split) == TouchLayout.Zone.SteerLeft);

                // Multitouch: finger A on the brake, finger B on the right chevron. Roles are
                // classified independently per finger (TouchDriveInput keeps them by id).
                bool multi = TouchLayout.Classify(b.center, b, split) == TouchLayout.Zone.Brake
                          && TouchLayout.Classify(rc, b, split) == TouchLayout.Zone.SteerRight;
                fails += Check(sb, c.name, "brake finger + right-steer finger resolve independently", multi);

                // Top-right HUD column: minimap | fullscreen over sound over pause, in canvas units of
                // the safe area (x from its left edge, y DOWN from its top), at the scale the minimap
                // is drawn on this class of device. Each case also runs with the fullscreen button
                // forced absent (iPhone Safari): the column then collapses to sound over pause.
                bool phone = Mathf.Min(c.w, c.h) <= RenaultRacer.UI.DeviceClass.PhoneMaxShortSideCss;
                int ovWas = RenaultRacer.UI.DeviceClass.Override;
                int fsWas = RenaultRacer.UI.FullscreenView.Override;
                RenaultRacer.UI.DeviceClass.Override = phone ? 1 : 0;
                try
                {
                    for (int fsOn = 1; fsOn >= 0; fsOn--)
                    {
                        RenaultRacer.UI.FullscreenView.Override = fsOn;
                        string tag = fsOn == 1 ? "[3 buttons] " : "[fullscreen absent] ";
                        float safeW = safe.width / scale;
                        float safeH = safe.height / scale;
                        Vector2 bs = RenaultRacer.UI.SoundToggleView.ButtonSize;
                        Rect map = Box(safeW - RenaultRacer.UI.HudView.MinimapLeftInset(), RenaultRacer.UI.HudView.EdgeMargin,
                                       safeW - RenaultRacer.UI.HudView.MinimapRightInset(), RenaultRacer.UI.HudView.MinimapBottomInset());
                        Vector2 fo = RenaultRacer.UI.FullscreenView.ButtonOffset;
                        Vector2 so = RenaultRacer.UI.SoundToggleView.ButtonOffset;
                        Vector2 po = RenaultRacer.UI.PauseView.ButtonOffset;
                        Rect fs = Box(safeW + fo.x - bs.x, -fo.y, safeW + fo.x, -fo.y + bs.y);
                        Rect snd = Box(safeW + so.x - bs.x, -so.y, safeW + so.x, -so.y + bs.y);
                        Rect pau = Box(safeW + po.x - bs.x, -po.y, safeW + po.x, -po.y + bs.y);
                        Rect fsHit = Grow(fs, RenaultRacer.UI.FullscreenView.HitMargin(scale), scale);
                        Rect sndHit = Grow(snd, RenaultRacer.UI.SoundToggleView.HitMargin(scale), scale);
                        Rect pauHit = Grow(pau, RenaultRacer.UI.PauseView.ButtonHitMargin(scale), scale);
                        Vector2 ts = RenaultRacer.UI.HudView.TimerSize;
                        Rect timer = Box(safeW * 0.5f - ts.x * 0.5f, RenaultRacer.UI.HudView.EdgeMargin,
                                         safeW * 0.5f + ts.x * 0.5f, RenaultRacer.UI.HudView.EdgeMargin + ts.y);
                        float logoH = phone ? 80f : 52f;
                        Rect logo = Box(10f, 10f, 10f + logoH * 4.3f, 10f + logoH);
                        // Sound caption: 240 x 22 box (the worst case), right-aligned under the column
                        // and under the minimap where that hangs lower.
                        float hintX, hintTop;
                        RenaultRacer.UI.SoundToggleView.HintPlacement(out hintX, out hintTop);
                        Rect hint = Box(safeW + so.x + hintX - 240f, hintTop, safeW + so.x + hintX, hintTop + 22f);
                        // Right steer button (the chevron), safe-area units, y down (screen y is up).
                        float chevHalf = 56f;   // 112-unit chevron
                        float cx = (rc.x - safe.xMin) / scale, cy = (c.h - c.top - rc.y) / scale;
                        Rect chev = Box(cx - chevHalf, cy - chevHalf, cx + chevHalf, cy + chevHalf);

                        sb.AppendLine("  " + tag + (phone ? "(phone map x1.28) " : "")
                                      + "map x " + map.xMin.ToString("F0") + ".." + map.xMax.ToString("F0") + " y " + map.yMin.ToString("F0") + ".." + map.yMax.ToString("F0")
                                      + " | col x " + snd.xMin.ToString("F0") + ".." + snd.xMax.ToString("F0")
                                      + " fs y " + (fsOn == 1 ? fs.yMin.ToString("F0") + ".." + fs.yMax.ToString("F0") : "-")
                                      + " snd y " + snd.yMin.ToString("F0") + ".." + snd.yMax.ToString("F0")
                                      + " pause y " + pau.yMin.ToString("F0") + ".." + pau.yMax.ToString("F0")
                                      + " | scale " + scale.ToString("F2")
                                      + " | hint y " + hint.yMin.ToString("F0") + ".." + hint.yMax.ToString("F0")
                                      + " | right chevron x " + chev.xMin.ToString("F0") + ".." + chev.xMax.ToString("F0") + " y " + chev.yMin.ToString("F0") + ".." + chev.yMax.ToString("F0")
                                      + " | safe " + safeW.ToString("F0") + "x" + safeH.ToString("F0"));
                        string n = c.name + " " + tag;
                        fails += Check(sb, n, "order top-down: [fullscreen] > sound > pause, same right edge",
                            snd.yMax <= pau.yMin && Mathf.Approximately(snd.xMax, pau.xMax)
                            && (fsOn == 0 || (fs.yMax <= snd.yMin && Mathf.Approximately(fs.xMax, snd.xMax))));
                        fails += Check(sb, n, "column entirely right of the minimap (>= 12 units)", snd.xMin - map.xMax >= 12f);
                        fails += Check(sb, n, "top of the column aligned with the minimap", Mathf.Approximately((fsOn == 1 ? fs : snd).yMin, map.yMin));
                        fails += Check(sb, n, "buttons inside the safe area", snd.xMax <= safeW && pau.xMax <= safeW && (fsOn == 0 || fs.xMax <= safeW) && pau.yMax <= safeH);
                        fails += Check(sb, n, "button sizes >= 64 units", bs.x >= 64f && bs.y >= 64f);
                        Rect[] hits = fsOn == 1 ? new[] { fsHit, sndHit, pauHit } : new[] { sndHit, pauHit };
                        bool hitOverlap = false;
                        for (int i = 0; i < hits.Length; i++)
                            for (int j = i + 1; j < hits.Length; j++)
                                if (hits[i].Overlaps(hits[j])) hitOverlap = true;
                        fails += Check(sb, n, "hit rects do not overlap each other", !hitOverlap);
                        float minHitPx = 1e9f;
                        foreach (Rect hr in hits) minHitPx = Mathf.Min(minHitPx, hr.width * scale, hr.height * scale);
                        sb.AppendLine("    smallest hit rect side " + minHitPx.ToString("F0") + " px (44 px target; the canvas scale limits it on small phones)");
                        fails += Check(sb, n, "hit rects >= 36 px on screen", minHitPx >= 36f);
                        bool clearMap = true, clearTimer = true, clearLogo = true, clearChev = true;
                        foreach (Rect hr in hits)
                        {
                            if (hr.Overlaps(map)) clearMap = false;
                            if (hr.Overlaps(timer)) clearTimer = false;
                            if (hr.Overlaps(logo)) clearLogo = false;
                            if (hr.Overlaps(chev)) clearChev = false;
                        }
                        fails += Check(sb, n, "hit rects clear of the minimap", clearMap);
                        fails += Check(sb, n, "minimap clear of the timer and logo", !map.Overlaps(timer) && !map.Overlaps(logo) && !logo.Overlaps(timer));
                        fails += Check(sb, n, "hit rects clear of the timer and logo", clearTimer && clearLogo);
                        fails += Check(sb, n, "hit rects clear of the right steer button", clearChev);
                        fails += Check(sb, n, "sound caption clear of the pause button and the right steer button", !hint.Overlaps(pau) && !hint.Overlaps(chev));
                        fails += Check(sb, n, "sound caption clear of the minimap", !hint.Overlaps(map));
                        fails += Check(sb, n, "minimap inside the safe area", map.xMin >= 0f);
                    }
                }
                finally { RenaultRacer.UI.DeviceClass.Override = ovWas; RenaultRacer.UI.FullscreenView.Override = fsWas; }
            }
            sb.AppendLine(fails == 0 ? "ALL CHECKS PASSED" : fails + " check(s) **FAIL**ed");
            return sb.ToString();
        }

        // y-down box from corner coordinates.
        private static Rect Box(float x0, float y0, float x1, float y1) { return new Rect(x0, y0, x1 - x0, y1 - y0); }

        // Unity's RectangleContainsScreenPoint margin is (left, top, right, bottom) in screen px,
        // negative grows; the rect here is in canvas units with y down, so top grows toward 0.
        private static Rect Grow(Rect r, Vector4 m, float scale)
        {
            float l = -m.x / scale, t = -m.y / scale, rt = -m.z / scale, b = -m.w / scale;
            return new Rect(r.xMin - l, r.yMin - t, r.width + l + rt, r.height + t + b);
        }

        private static int Check(StringBuilder sb, string c, string what, bool ok)
        {
            sb.AppendLine("  " + (ok ? "PASS " : "**FAIL** ") + what);
            return ok ? 0 : 1;
        }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = RunAll(); }
            catch (Exception e) { Console.WriteLine("touch layout probe threw: " + e); EditorApplication.Exit(2); return; }
            Console.WriteLine(report);
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/touch-layout-probe.txt", report);
            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine(clean ? "[touch layout probe] ALL PASS" : "[touch layout probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }
    }
}




