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
    public static class EditorFrameCapture
    {
        private static List<string> _targets;
        private static int _w = 1280, _h = 720, _frame, _ti, _phase, _waitUntil;
        private static RenderTexture _rt;
        private static bool _drive;
        private static int _lastLoggedSeg = -1, _shots;
        private static System.Reflection.FieldInfo _offF;

        public static void RunFromCommandLine()
        {
            string[] a = System.Environment.GetCommandLineArgs();
            string at = "s1100";
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] == "-captureAt") at = a[i + 1];
                if (a[i] == "-captureSize") { var p = a[i + 1].Split('x'); _w = int.Parse(p[0]); _h = int.Parse(p[1]); }
            }
            _targets = new List<string>(at.Split(','));
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
            if (_rt == null)
            {
                _rt = new RenderTexture(_w, _h, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = _rt;
                cam.aspect = (float)_w / _h;
            }
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
                    Debug.Log("[EditorFrameCapture] drive seg " + seg + " z " + car.Sim.Z.ToString("F0") + " offset " + off.ToString("F5") + " dt " + Time.deltaTime.ToString("F4"));
                    if ((seg >= 1120 && seg <= 1135) || (seg >= 1180 && seg <= 1192))
                    {
                        cam.Render();
                        RenderTexture.active = _rt;
                        var t2 = new Texture2D(_w, _h, TextureFormat.RGB24, false);
                        t2.ReadPixels(new Rect(0, 0, _w, _h), 0, 0); t2.Apply();
                        RenderTexture.active = null;
                        Directory.CreateDirectory("Logs/editor-frames");
                        File.WriteAllBytes("Logs/editor-frames/drive_" + seg + ".png", t2.EncodeToPNG());
                    }
                }
                if (seg > 1200) Finish();
                return;
            }
            if (_phase == 2)
            {
                if (_ti >= _targets.Count) { Finish(); return; }
                if (Time.frameCount < _waitUntil + 30) return;
                hud.DebugJump(_targets[_ti]);
                _waitUntil = Time.frameCount + 6;
                _phase = 3;
                return;
            }
            if (_phase == 3 && Time.frameCount >= _waitUntil)
            {
                cam.Render();
                RenderTexture.active = _rt;
                var tex = new Texture2D(_w, _h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, _w, _h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                Directory.CreateDirectory("Logs/editor-frames");
                File.WriteAllBytes("Logs/editor-frames/" + _targets[_ti] + "_" + _w + "x" + _h + ".png", tex.EncodeToPNG());
                Debug.Log("[EditorFrameCapture] " + _targets[_ti] + " z=" + Object.FindAnyObjectByType<RenaultRacer.Vehicle.CarController>().Sim.Z
                          + " gfx=" + SystemInfo.graphicsDeviceType);
                _ti++;
                _phase = 2;
            }
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            Debug.Log("[EditorFrameCapture] done");
            EditorApplication.Exit(0);
        }
    }
}
