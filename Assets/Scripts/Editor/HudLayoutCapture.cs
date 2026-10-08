using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Track;
using RenaultRacer.UI;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Editor-only: builds the real HudView on a 1280x720 / match 0.5 CanvasScaler (as Main.unity)
    /// over a sky/asphalt backdrop and renders Logs/hud-WxH-{phone|desk}.png at several sizes.
    ///   -executeMethod RenaultRacer.EditorTools.HudLayoutCapture.RunFromCommandLine
    /// Needs a graphics device (no -nographics).
    /// </summary>
    public static class HudLayoutCapture
    {
        private static readonly int[][] Sizes = { new[] {1280,720}, new[] {844,390}, new[] {932,430}, new[] {667,375} };

        public static void RunFromCommandLine()
        {
            try { Run(); }
            catch (Exception e) { Console.WriteLine("[HudCapture] threw: " + e); EditorApplication.Exit(2); return; }
            EditorApplication.Exit(0);
        }

        private static void Run()
        {
            Directory.CreateDirectory("Logs");
            TrackModel track = TrackBuilder.BuildMainTrack();
            foreach (int[] s in Sizes)
            {
                DeviceClass.Override = Mathf.Min(s[0], s[1]) <= 520 ? 1 : 0;
                Shoot(track, s[0], s[1], Mathf.Min(s[0], s[1]) <= 520 ? "phone" : "desk");
                if (s[0] == 844)
                {
                    DeviceClass.Override = 0;
                    Shoot(track, s[0], s[1], "forcedDesk");
                }
            }
            DeviceClass.Override = -1;
        }

        private static void Shoot(TrackModel track, int w, int h, string tag)
        {
            var root = new GameObject("CapRoot");
            var camGo = new GameObject("Cam"); camGo.transform.SetParent(root.transform);
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.55f, 0.75f, 0.95f);
            cam.orthographic = true; cam.orthographicSize = 5; cam.transform.position = new Vector3(0, 0, -10);
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.transform.SetParent(root.transform);
            q.transform.position = new Vector3(0, -5, 0); q.transform.localScale = new Vector3(40, 10, 1);
            q.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(Shader.Find("Unlit/Color")) { color = new Color(0.16f, 0.16f, 0.17f) };

            var cgo = new GameObject("Canvas", typeof(RectTransform));
            cgo.transform.SetParent(root.transform);
            Canvas canvas = cgo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = cam; canvas.planeDistance = 1;
            var scaler = cgo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720); scaler.matchWidthOrHeight = 0.5f;
            scaler.referencePixelsPerUnit = 100;

            var hudGo = new GameObject("HudHost", typeof(RectTransform));
            hudGo.transform.SetParent(cgo.transform, false);
            UiPrimitives.Fill((RectTransform)hudGo.transform, 0f);
            var hud = hudGo.AddComponent<HudView>();
            hud.Build(hudGo.transform, track);

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.aspect = (float)w / h;
            Canvas.ForceUpdateCanvases();
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = null;
            string path = "Logs/hud-" + w + "x" + h + "-" + tag + ".png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Debug.Log("[HudCapture] " + path + " scale=" + canvas.scaleFactor);
            UnityEngine.Object.DestroyImmediate(root);
            rt.Release();
        }
    }
}
