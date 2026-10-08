using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Editor-only: renders the REAL RoadRenderer to PNGs (Logs/wall-*.png) at a straight,
    /// a left bend, a right bend, a crest, and a near close-up, so the tyre wall can be
    /// judged by eye. Needs a graphics device (not -nographics). Not a pass/fail probe.
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod RenaultRacer.EditorTools.WallCaptureProbe.RunFromCommandLine
    /// Env WALLCAP_TAG sets the filename tag (default "after").
    /// </summary>
    public static class WallCaptureProbe
    {
        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";
        private const int W = 1280, H = 720;

        public static void RunFromCommandLine()
        {
            try { Run(); }
            catch (Exception e) { Console.WriteLine("[WallCapture] threw: " + e); EditorApplication.Exit(2); return; }
            EditorApplication.Exit(0);
        }

        private static void Run()
        {
            string tag = Environment.GetEnvironmentVariable("WALLCAP_TAG");
            if (string.IsNullOrEmpty(tag)) tag = "after";
            TrackModel t = TrackBuilder.BuildMainTrack();
            int n = t.Count;

            int bestL = 0, bestR = 0, bestC = 0, bestS = 0;
            float cl = 0, cr = 0, cc = 0, cs = 1e9f;
            for (int i = 20; i < n - 40; i++)
            {
                float mean = 0, absMean = 0, dy = 0;
                for (int k = 0; k < 15; k++) { mean += t[i + k].curve; absMean += Mathf.Abs(t[i + k].curve); }
                mean /= 15f; absMean /= 15f;
                if (mean > cl) { cl = mean; bestL = i; }
                if (-mean > cr) { cr = -mean; bestR = i; }
                if (absMean < 0.01f)
                {
                    float flat = 0; for (int k = 0; k < 20; k++) flat += Mathf.Abs(t[i + k + 1].y - t[i + k].y);
                    if (flat < cs) { cs = flat; bestS = i; }
                }
                float s0 = 0, s1 = 0;
                for (int k = 1; k <= 5; k++) { s0 += t[i - k + 1].y - t[i - k].y; s1 += t[i + k].y - t[i + k - 1].y; }
                dy = (s0 - s1) / 5f;
                if (dy > cc) { cc = dy; bestC = i; }
            }
            Console.WriteLine("[WallCapture] straight " + bestS + " left " + bestL + " right " + bestR + " crest " + bestC);

            var camGo = new GameObject("CapCam"); camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5f; cam.aspect = (float)W / H;
            cam.transform.position = new Vector3(0, 0, -10); cam.nearClipPlane = 0.3f; cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.28f, 0.6f, 0.86f);
            var roadGo = new GameObject("CapRoad", typeof(MeshFilter), typeof(MeshRenderer));
            var road = roadGo.AddComponent<RoadRenderer>();
            road.Initialise(t, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            cam.targetTexture = rt;
            Directory.CreateDirectory("Logs");
            Shot(road, cam, rt, tex, t, tag, "straight", bestS * 200f, 0f);
            Shot(road, cam, rt, tex, t, tag, "left", bestL * 200f, 0f);
            Shot(road, cam, rt, tex, t, tag, "right", bestR * 200f, 0f);
            Shot(road, cam, rt, tex, t, tag, "crest", (bestC - 6) * 200f, 0f);
            Shot(road, cam, rt, tex, t, tag, "close", bestS * 200f, 0.78f);
            Shot(road, cam, rt, tex, t, tag, "closeleft", bestS * 200f, -0.78f);
            Shot(road, cam, rt, tex, t, tag, "bendclose", bestR * 200f, 0.7f);
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(roadGo); UnityEngine.Object.DestroyImmediate(camGo);
        }

        private static void Shot(RoadRenderer road, Camera cam, RenderTexture rt, Texture2D tex, TrackModel t,
                                 string tag, string name, float z, float xFrac)
        {
            float lim = xFrac >= 0 ? t.BarrierRightAt(z) : Mathf.Abs(t.BarrierLeftAt(z));
            road.CameraZ = z; road.CameraX = xFrac * lim * (xFrac >= 0 ? 1f : 1f);
            if (xFrac < 0) road.CameraX = -Mathf.Abs(xFrac) * lim;
            road.Rebuild((float)W / H);
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes("Logs/wall-" + tag + "-" + name + ".png", tex.EncodeToPNG());
        }
    }
}
