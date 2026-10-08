using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.InputSystem;
using RenaultRacer.Track;
using RenaultRacer.UI;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Editor-only capture (not a pass/fail probe): renders the touch controls over the
    /// real road at phone sizes, and the portrait rotate gate, to Logs/mobileui-*.png.
    ///   -executeMethod RenaultRacer.EditorTools.MobileUiCapture.RunFromCommandLine
    /// Needs a graphics device.
    /// </summary>
    public static class MobileUiCapture
    {
        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";

        public static void RunFromCommandLine()
        {
            try { Run(); }
            catch (Exception e) { Console.WriteLine("[MobileUiCapture] threw: " + e); EditorApplication.Exit(2); return; }
            EditorApplication.Exit(0);
        }

        private static void Run()
        {
            Directory.CreateDirectory("Logs");
            TrackModel t = TrackBuilder.BuildMainTrack();
            int bestS = 0, bestR = 0; float cs = 1e9f, cr = 0;
            for (int i = 20; i < t.Count - 40; i++)
            {
                float mean = 0, absMean = 0;
                for (int k = 0; k < 15; k++) { mean += t[i + k].curve; absMean += Mathf.Abs(t[i + k].curve); }
                mean /= 15f; absMean /= 15f;
                if (-mean > cr) { cr = -mean; bestR = i; }
                if (absMean < 0.01f)
                {
                    float flat = 0; for (int k = 0; k < 20; k++) flat += Mathf.Abs(t[i + k + 1].y - t[i + k].y);
                    if (flat < cs) { cs = flat; bestS = i; }
                }
            }

            // width, height, insets (top, right, bottom, left) in screen px.
            var cases = new[]
            {
                new Case(844, 390, new Vector4(0, 47, 21, 47)),
                new Case(932, 430, new Vector4(0, 59, 21, 59)),
                new Case(915, 412, new Vector4(0, 48, 21, 48)),
                new Case(667, 375, Vector4.zero),
                new Case(800, 360, new Vector4(0, 44, 18, 44)),   // 20:9
                new Case(780, 360, new Vector4(0, 47, 21, 47)),   // 19.5:9
                new Case(1280, 720, Vector4.zero),
                new Case(1920, 1080, Vector4.zero),
            };
            foreach (var c in cases)
            {
                ControlsShot(t, bestS, "straight", c.w, c.h, c.ins, false);
                if (c.w == 844 || c.w == 1920)
                {
                    ControlsShot(t, bestS, "overlay", c.w, c.h, c.ins, false);
                    ControlsShot(t, bestR, "bend", c.w, c.h, c.ins, false);
                    ControlsShot(t, bestS, "straight-pressed", c.w, c.h, c.ins, true);
                }
            }
            DeviceClass.Override = -1;
            FullscreenView.Override = -1;

            GateShot(390, 844, "a");
            GateShot(360, 800, "a");
            GateShot(375, 667, "a");
            GateShot(360, 740, "a");
        }

        private struct Case
        {
            public int w, h; public Vector4 ins;
            public Case(int w, int h, Vector4 ins) { this.w = w; this.h = h; this.ins = ins; }
        }

        private static Canvas MakeCanvas(Camera uiCam, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            var c = go.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceCamera; c.worldCamera = uiCam; c.planeDistance = 1f;
            var cs = go.GetComponent<CanvasScaler>();
            cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1280, 720);
            cs.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            cs.matchWidthOrHeight = 0.5f;
            cs.referencePixelsPerUnit = 100;
            return c;
        }

        private static Camera MakeUiCam(RenderTexture rt, bool clear, Color bg)
        {
            var g = new GameObject("UiCam"); var cam = g.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5; cam.nearClipPlane = 0.1f; cam.farClipPlane = 10f;
            cam.transform.position = new Vector3(5000, 5000, -5);
            cam.clearFlags = clear ? CameraClearFlags.SolidColor : CameraClearFlags.Depth;
            cam.backgroundColor = bg; cam.targetTexture = rt;
            return cam;
        }

        private static void Save(RenderTexture rt, int w, int h, string file)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes("Logs/" + file, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void ControlsShot(TrackModel t, int seg, string tag, int w, int h, Vector4 ins, bool pressed)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var camGo = new GameObject("CapCam"); camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5f; cam.aspect = (float)w / h;
            cam.transform.position = new Vector3(0, 0, -10); cam.nearClipPlane = 0.3f; cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.28f, 0.6f, 0.86f);
            cam.targetTexture = rt;
            var roadGo = new GameObject("CapRoad", typeof(MeshFilter), typeof(MeshRenderer));
            var road = roadGo.AddComponent<RoadRenderer>();
            road.Initialise(t, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));
            road.CameraZ = seg * 200f; road.CameraX = 0f; road.Rebuild((float)w / h);
            cam.Render();

            var uiCam = MakeUiCam(rt, false, Color.clear);
            uiCam.aspect = (float)w / h;
            var canvas = MakeCanvas(uiCam, "CapCanvas");
            Canvas.ForceUpdateCanvases();
            float scale = canvas.scaleFactor;

            bool phone = Mathf.Min(w, h) <= 520;
            DeviceClass.Override = phone ? 1 : 0;
            FullscreenView.Override = 1;   // captured with the fullscreen button present
            var hostGo = new GameObject("HudHost", typeof(RectTransform));
            hostGo.transform.SetParent(canvas.transform, false);
            var host = (RectTransform)hostGo.transform;
            host.anchorMin = Vector2.zero; host.anchorMax = Vector2.one;
            host.offsetMin = new Vector2(ins.w, ins.z) / scale; host.offsetMax = new Vector2(-ins.y, -ins.x) / scale;
            hostGo.AddComponent<HudView>().Build(host, t);
            // The real PauseView (its Update does not run in the editor, so the shown state is set here).
            var dirGo = new GameObject("CapDirector");
            FullscreenView.Create(host);
            var pv = PauseView.Create(host, dirGo.AddComponent<RenaultRacer.Core.GameDirector>());
            bool overlay = tag == "overlay";
            pv.ButtonRect.gameObject.SetActive(!overlay);
            pv.ResumeRect.parent.gameObject.SetActive(overlay);
            // Sound button stand-in at its real rect (the real one needs the AudioDirector).
            var snd = UiPrimitives.Panel(host, "SoundStandIn", UiPrimitives.Disc, new Color(0.067f, 0.075f, 0.09f, 0.62f));
            UiPrimitives.Place(snd.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                               SoundToggleView.ButtonOffset, SoundToggleView.ButtonSize);

            Rect safe = new Rect(ins.w, ins.z, w - ins.w - ins.y, h - ins.x - ins.z);
            var d = new TouchDriveInput();
            Rect b = TouchLayout.BrakeRect(w, h, safe, true, d.brakeSize, d.brakeMargin, d.brakeInward);
            Vector2 lc, rc; TouchLayout.ChevronCentres(w, h, safe, 0.09f, 0.55f, out lc, out rc);

            Place(canvas, "brake", pressed ? "brake_pressed" : "brake_idle", b.center, b.size, scale, pressed ? 1f : 0.9f);
            float rest = TouchControlsView.DefaultChevronRestAlpha, prs = TouchControlsView.DefaultChevronPressedAlpha;
            Vector2 cs = new Vector2(112, 112) * scale;
            Place(canvas, "cl", "steer_left_idle", lc, cs, scale, rest);
            if (pressed) Place(canvas, "cr", "steer_right_pressed", rc, cs, scale, prs);
            else Place(canvas, "cr", "steer_right_idle", rc, cs, scale, rest);

            // As in the game, the drive controls sit BEHIND the HUD and panels.
            foreach (string n in new[] { "brake", "cl", "cr" })
            {
                Transform ct = canvas.transform.Find(n);
                if (ct != null) ct.SetAsFirstSibling();
            }
            Canvas.ForceUpdateCanvases();
            uiCam.Render();
            Save(rt, w, h, "ui-" + w + "x" + h + "-" + tag + ".png");
            UnityEngine.Object.DestroyImmediate(roadGo); UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(uiCam.gameObject); UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            UnityEngine.Object.DestroyImmediate(dirGo);
            rt.Release();
        }

        // sizePx is in screen pixels; converted to canvas units.
        private static void Place(Canvas canvas, string name, string sprite, Vector2 centerPx, Vector2 sizePx, float scale, float alpha)
        {
            var img = UiPrimitives.Panel(canvas.transform, name, UiArt.Get(sprite), new Color(1, 1, 1, alpha));
            var r = img.rectTransform;
            r.anchorMin = r.anchorMax = Vector2.zero; r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = sizePx / scale; r.anchoredPosition = centerPx / scale;
        }

        private static void GateShot(int w, int h, string tag)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            var uiCam = MakeUiCam(rt, true, Color.black);
            uiCam.aspect = (float)w / h;
            var canvas = MakeCanvas(uiCam, "GateCanvas");
            var gate = canvas.gameObject.AddComponent<RotateGate>();
            var bf = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(RotateGate).GetMethod("Awake", bf).Invoke(gate, null);
            typeof(RotateGate).GetMethod("SetGated", bf).Invoke(gate, new object[] { true });
            Canvas.ForceUpdateCanvases();
            typeof(RotateGate).GetMethod("Animate", bf).Invoke(gate, null);
            Canvas.ForceUpdateCanvases();
            uiCam.Render();
            Save(rt, w, h, "ui-gate-" + w + "x" + h + ".png");
            UnityEngine.Object.DestroyImmediate(uiCam.gameObject); UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            rt.Release();
        }
    }
}

