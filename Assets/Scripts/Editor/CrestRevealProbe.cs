using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// THE T06-T07 SKY POP, measured. Walks the REAL RoadRenderer along the production
    /// circuit one 200-unit frame at a time (racing speed at 60 FPS) and asks, for every
    /// segment in view, how many pixels of a tall thing standing on it are visible - a
    /// 3,600-unit spruce, the tallest prop - and how much that changes in ONE frame.
    ///
    /// A thing rising over a crest grows smoothly from nothing. A thing culled whole while its base is
    /// hidden and then drawn whole the frame its base appears jumps by its entire visible
    /// height, against the sky: that is the pop. Both rules are scored from the same
    /// samples - the old cull ("base hidden, draw nothing") and the shipped crest clip
    /// ("draw what is above the crest line") - so the report shows the fix, not just the
    /// result. Gated on the shipped rule only.
    ///
    ///   Unity.exe -batchmode -quit -nographics -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.CrestRevealProbe.RunFromCommandLine
    /// </summary>
    public static class CrestRevealProbe
    {
        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";
        private const string LogPath = "Logs/crest-reveal-probe.txt";
        private const float FrameZ = 200f;           // one frame at ~12,000 u/s, 60 FPS
        private const float TreeUnits = 3600f;       // the tallest roadside prop
        private const float MaxJumpPx = 20f;          // on the 720-px virtual screen
        private const int MinDepth = 8, MaxDepth = 150;

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e) { Console.WriteLine("crest reveal probe threw: " + e); EditorApplication.Exit(2); return; }
            Console.WriteLine(report);
            try { Directory.CreateDirectory("Logs"); File.WriteAllText(LogPath, report); }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }
            bool clean = report.IndexOf("**FAIL**", StringComparison.Ordinal) < 0;
            Console.WriteLine(clean ? "[crest reveal probe] ALL PASS" : "[crest reveal probe] FAILURES ABOVE");
            EditorApplication.Exit(clean ? 0 : 1);
        }

        [MenuItem("Renault Racer/Crest reveal probe - do props pop over crests", false, 44)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static string Run()
        {
            TrackModel track = TrackBuilder.BuildMainTrack();
            var sb = new StringBuilder(4096);
            sb.AppendLine("================ CREST REVEAL ================");
            sb.AppendLine("elevation scale " + TrackBuilder.ElevationScale.ToString("F3")
                          + "   a " + TreeUnits.ToString("F0") + "-unit prop on every segment, depth "
                          + MinDepth + "-" + MaxDepth + ", one frame = " + FrameZ.ToString("F0") + " units");

            var camGo = new GameObject("ProbeCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5f; cam.aspect = 16f / 9f;
            var roadGo = new GameObject("ProbeRoad", typeof(MeshFilter), typeof(MeshRenderer));
            var road = roadGo.AddComponent<RoadRenderer>();
            bool ok = true;
            try
            {
                road.Initialise(track, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));
                float ppu = road.virtualHeight / (2f * cam.orthographicSize);
                float anisotropy = 1f / Mathf.Max(0.1f, road.referenceAspect);

                // The T05-T09 window holds the reported pop; the whole lap is scored too.
                ok &= Window(sb, road, track, ppu, anisotropy, "T05-T09  (Z 200,000-300,000)", 200000f, 300000f);
                ok &= Window(sb, road, track, ppu, anisotropy, "whole lap", 0f, track.Length - 2f * FrameZ);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(roadGo);
                UnityEngine.Object.DestroyImmediate(camGo);
            }
            sb.AppendLine("CREST REVEAL VERDICT = " + (ok ? "PASS" : "**FAIL**"));
            return sb.ToString();
        }

        private static bool Window(StringBuilder sb, RoadRenderer road, TrackModel track, float ppu,
                                   float anisotropy, string label, float z0, float z1)
        {
            int n = track.Count;
            var prevOld = new float[n];
            var prevNew = new float[n];
            var curOld = new float[n];
            var curNew = new float[n];
            var prevIn = new bool[n];
            var curIn = new bool[n];
            bool primed = false;
            int popsOld = 0, popsNew = 0, frames = 0;
            float worstOld = 0f, worstNew = 0f, worstOldZ = 0f, worstNewZ = 0f;

            for (float z = z0; z <= z1; z += FrameZ)
            {
                road.CameraZ = z;
                road.CameraX = 0f;
                road.Rebuild(16f / 9f);
                Array.Clear(curOld, 0, n);
                Array.Clear(curNew, 0, n);
                Array.Clear(curIn, 0, n);
                int count = road.SampleCount;
                for (int i = 0; i < count; i++)
                {
                    RoadRenderer.SegmentSample s = road.GetSample(i);
                    if (s.depth < MinDepth || s.depth > MaxDepth) continue;
                    curIn[s.index] = true;
                    float h = s.halfWidth / road.roadHalfWidth * TreeUnits * anisotropy;   // world units
                    bool baseHidden = s.worldY < s.clipY - 0.001f;
                    // Old: culled whole while the base is hidden (and never sampled at all
                    // on a band that is not drawn).
                    curOld[s.index] = (s.hidden || baseHidden) ? 0f : h * ppu;
                    // New: everything above the crest line.
                    float bottom = baseHidden ? s.clipY : s.worldY;
                    curNew[s.index] = Mathf.Max(0f, s.worldY + h - bottom) * ppu;
                }
                if (primed)
                {
                    for (int k = 0; k < n; k++)
                    {
                        // A POP: in view in both frames, showing NOTHING in the first and a
                        // lump of itself in the second. Ordinary perspective growth of a
                        // thing already showing is not one, and neither is the far end of
                        // the window moving out by a segment.
                        if (!prevIn[k] || !curIn[k]) continue;
                        float jo = prevOld[k] <= 0.5f ? curOld[k] : 0f;
                        float jn = prevNew[k] <= 0.5f ? curNew[k] : 0f;
                        if (jo > MaxJumpPx) popsOld++;
                        if (jn > MaxJumpPx) popsNew++;
                        if (jo > worstOld) { worstOld = jo; worstOldZ = z; }
                        if (jn > worstNew) { worstNew = jn; worstNewZ = z; }
                    }
                }
                var t = prevOld; prevOld = curOld; curOld = t;
                t = prevNew; prevNew = curNew; curNew = t;
                var b = prevIn; prevIn = curIn; curIn = b;
                primed = true;
                frames++;
            }
            bool pass = worstNew <= MaxJumpPx;
            sb.AppendLine("  " + label + ", " + frames + " frames");
            sb.AppendLine("    old whole-cull rule: " + popsOld + " pops over " + MaxJumpPx.ToString("F0") + " px, worst "
                          + worstOld.ToString("F1") + " px at Z " + worstOldZ.ToString("F0"));
            sb.AppendLine("    shipped crest clip : " + popsNew + " pops over " + MaxJumpPx.ToString("F0") + " px, worst "
                          + worstNew.ToString("F1") + " px at Z " + worstNewZ.ToString("F0")
                          + "   " + (pass ? "PASS" : "**FAIL**"));
            return pass;
        }
    }
}
