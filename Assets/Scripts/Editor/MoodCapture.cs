using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// DIAGNOSTIC (sandbox): plays Main.unity in the EDITOR, jumps the car to given track
    /// positions with DebugHud.DebugJump - the same hook the LAN build exposes - and renders
    /// Camera.main to PNG, so an Editor frame and a browser frame of the same spot can be
    /// compared pixel for pixel. World only: overlay UI is not in the camera's target.
    ///   -executeMethod RenaultRacer.EditorTools.EditorFrameCapture.RunFromCommandLine
    ///   -captureAt s1100,s1140  -captureSize 1280x720
    /// </summary>
    public static class MoodCapture
    {
        private static List<string> _targets;
        private static int _w = 1280, _h = 720, _frame, _ti, _phase, _waitUntil;
        private static RenderTexture _rt;
        private static bool _drive; private static string _tag; private static bool _applied;
        private static int _lastLoggedSeg = -1, _shots;
        private static System.Reflection.FieldInfo _offF;
        private struct Variant { public string tag, look; public int w, h; }
        private static List<Variant> _variants = new List<Variant>();
        private static int _vi = -1, _variantWait;

        public static void RunFromCommandLine()
        {
            string[] a = System.Environment.GetCommandLineArgs();
            string at = System.Environment.GetEnvironmentVariable("MOOD_AT");
            if (string.IsNullOrEmpty(at)) at = "s60,T03,T07,s490,s1100";
            _tag = System.Environment.GetEnvironmentVariable("MOOD_TAG");
            if (string.IsNullOrEmpty(_tag)) _tag = "before";
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] == "-captureAt") at = a[i + 1];
                if (a[i] == "-captureSize") { var p = a[i + 1].Split('x'); _w = int.Parse(p[0]); _h = int.Parse(p[1]); }
            }
            _targets = new List<string>(at.Split(','));
            // MOOD_VARIANTS="tag:look:WxH,..." ; look = off|subtle|medium|strong|zero (medium at amount 0)
            string vs = System.Environment.GetEnvironmentVariable("MOOD_VARIANTS");
            if (string.IsNullOrEmpty(vs)) vs = _tag + ":keep:" + _w + "x" + _h;
            foreach (var v in vs.Split(','))
            {
                var p = v.Split(':'); var sz = p[2].Split('x');
                _variants.Add(new Variant { tag = p[0], look = p[1], w = int.Parse(sz[0]), h = int.Parse(sz[1]) });
            }
            _drive = System.Array.IndexOf(a, "-captureDrive") >= 0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            _frame++;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (!_applied) { _applied = true; ApplyMood(); }
            if (_vi < 0 || (_phase == 2 && _ti >= _targets.Count && _vi + 1 < _variants.Count)) { StartVariant(cam); return; }
            if (Time.frameCount < _variantWait) return;
            var director = Object.FindAnyObjectByType<RenaultRacer.Core.GameDirector>();
            var hud = Object.FindAnyObjectByType<RenaultRacer.UI.DebugHud>();
            if (director == null || hud == null) return;

            if (_phase == 0 && Time.frameCount > 30) { director.StartFromReady(); _phase = 1; return; }
            if (_phase == 1)
            {
                if (director.State != RenaultRacer.Core.GameState.Racing) return;
                _phase = 2;
            }
            if (_phase == 2 && _drive)
            {
                hud.DebugRail("on");
                var car = Object.FindAnyObjectByType<RenaultRacer.Vehicle.CarController>();
                var bg = Object.FindAnyObjectByType<RenaultRacer.Track.BackgroundView>();
                if (_offF == null) _offF = typeof(RenaultRacer.Track.BackgroundView).GetField("_scrollU", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int seg = (int)(car.Sim.Z / 200f);
                float off = bg != null && _offF != null ? (float)_offF.GetValue(bg) : float.NaN;
                if (seg != _lastLoggedSeg && (seg % 10 == 0 || (seg >= 1110 && seg <= 1200)))
                {
                    _lastLoggedSeg = seg;
                    Debug.Log("[MoodCapture] drive seg " + seg + " z " + car.Sim.Z.ToString("F0") + " offset " + off.ToString("F5") + " dt " + Time.deltaTime.ToString("F4"));
                    if ((seg >= 1120 && seg <= 1135) || (seg >= 1180 && seg <= 1192))
                    {
                        cam.Render();
                        RenderTexture.active = _rt;
                        var t2 = new Texture2D(_w, _h, TextureFormat.RGB24, false);
                        t2.ReadPixels(new Rect(0, 0, _w, _h), 0, 0); t2.Apply();
                        RenderTexture.active = null;
                        Directory.CreateDirectory("Logs/mood");
                        File.WriteAllBytes("Logs/mood/drive_" + seg + ".png", t2.EncodeToPNG());
                    }
                }
                if (seg > 1200) Finish();
                return;
            }
            if (_phase == 2)
            {
                if (_ti >= _targets.Count) { Finish(); return; }
                _tag = _variants[_vi].tag;
                if (Time.frameCount < _waitUntil + 30) return;
                hud.DebugJump(_targets[_ti]);
                Time.timeScale = 0f;      // freeze: identical car/road state in every variant
                _waitUntil = Time.frameCount + 5;
                _phase = 3;
                return;
            }
            if (_phase == 3 && Time.frameCount >= _waitUntil)
            {
                // The panorama scroll is path-dependent; pin it so variants are comparable.
                var bgv = Object.FindAnyObjectByType<RenaultRacer.Track.BackgroundView>();
                if (bgv != null)
                {
                    var bf = typeof(RenaultRacer.Track.BackgroundView);
                    bf.GetField("_scrollU", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(bgv, 0f);
                    bf.GetField("_hasLastZ", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(bgv, false);
                    bf.GetMethod("LateUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(bgv, null);
                }
                cam.Render();
                RenderTexture.active = _rt;
                var tex = new Texture2D(_w, _h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, _w, _h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                Directory.CreateDirectory("Logs/mood");
                File.WriteAllBytes("Logs/mood/" + _tag + "_" + _targets[_ti] + ".png", tex.EncodeToPNG());
                Debug.Log("[MoodCapture] " + _targets[_ti] + " z=" + Object.FindAnyObjectByType<RenaultRacer.Vehicle.CarController>().Sim.Z
                          + " gfx=" + SystemInfo.graphicsDeviceType);
                Time.timeScale = 1f;
                _ti++;
                _phase = 2;
            }
        }

        private static void StartVariant(Camera cam)
        {
            _vi++;
            var v = _variants[_vi];
            _w = v.w; _h = v.h; _tag = v.tag;
            cam.targetTexture = null;
            if (_rt != null) _rt.Release();
            _rt = new RenderTexture(_w, _h, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = _rt;
            cam.aspect = (float)_w / _h;
            var lg = RenaultRacer.Effects.LookGrade.Instance;
            if (lg != null && v.look != "keep")
            {
                if (v.look == "zero") { lg.ApplyPreset(RenaultRacer.Effects.LookGrade.Preset.Medium); lg.amount = 0f; }
                else lg.ApplyPreset((RenaultRacer.Effects.LookGrade.Preset)System.Enum.Parse(typeof(RenaultRacer.Effects.LookGrade.Preset), v.look, true));
                string ls = System.Environment.GetEnvironmentVariable("MOOD_LOOKSET");
                if (!string.IsNullOrEmpty(ls) && v.look != "off" && v.look != "zero")
                    foreach (var kv in ls.Split(';'))
                    {
                        var p = kv.Split('=');
                        var f = typeof(RenaultRacer.Effects.LookGrade).GetField(p[0]);
                        if (f == null) { Debug.Log("[MoodCapture] no look field " + p[0]); continue; }
                        f.SetValue(lg, System.Convert.ChangeType(p[1], f.FieldType, System.Globalization.CultureInfo.InvariantCulture));
                    }
            }
            Debug.Log("[MoodCapture] variant " + v.tag + " look=" + v.look + " " + _w + "x" + _h + " lg=" + (lg != null));
            _ti = 0;
            _variantWait = Time.frameCount + 20;
            _waitUntil = Time.frameCount;
        }

        // Task B hook: MOOD_PARAMS="field=value;field=value" set on the live RoadRenderer.
        private static void ApplyMood()
        {
            string ps = System.Environment.GetEnvironmentVariable("MOOD_PARAMS");
            if (string.IsNullOrEmpty(ps)) return;
            var rr = Object.FindAnyObjectByType<RenaultRacer.Track.RoadRenderer>();
            foreach (var kv in ps.Split(';'))
            {
                var p = kv.Split('=');
                var f = typeof(RenaultRacer.Track.RoadRenderer).GetField(p[0]);
                if (f == null) { Debug.Log("[MoodCapture] no field " + p[0]); continue; }
                f.SetValue(rr, System.Convert.ChangeType(p[1], f.FieldType, System.Globalization.CultureInfo.InvariantCulture));
                Debug.Log("[MoodCapture] set " + p[0] + "=" + p[1]);
            }
            // re-push the haze the way RoadRenderer / RoadsideSprites do at init
            Color h = rr.EffectiveHaze;
            foreach (var m in rr.GetComponent<MeshRenderer>().sharedMaterials) if (m != null) m.SetColor("_Haze", h);
            var pm = Resources.Load<Material>("Materials/PropSprite"); if (pm != null) pm.SetColor("_Haze", h);
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            Debug.Log("[MoodCapture] done");
            EditorApplication.Exit(0);
        }
    }
}
