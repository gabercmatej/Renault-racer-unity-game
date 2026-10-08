using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using RenaultRacer.Core;
using RenaultRacer.InputSystem;
using RenaultRacer.UI;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Play-mode verification of the user pause, on the REAL Main scene: real GameDirector,
    /// CarController, InputRouter, PauseView, AudioDirector. Not a copy of the logic.
    ///   -executeMethod RenaultRacer.EditorTools.PauseProbe.RunFromCommandLine
    /// Report: Logs/pause-probe.txt, pass marker "[pause probe] ALL PASS". Needs a graphics
    /// device (the UI canvas), like the capture tools.
    ///
    /// It checks: pause is refused outside countdown/racing; mid-race the car (Z, X, speed),
    /// race time, sector crossings and the boost timer are bit-identical after seconds of real
    /// frames; the rotate gate opening and closing while paused leaves it paused; on resume a
    /// held steer is ignored until released, then works; the clock does not jump on resume;
    /// a press on the pause button / resume button classifies as Role.None.
    /// </summary>
    public static class PauseProbe
    {
        /// <summary>Stands in for the keyboard so a held key can be simulated.</summary>
        private sealed class FakeKeys : IDriveInput
        {
            public float Steer;
            public float SteerAxis { get { return Steer; } }
            public bool BrakeHeld { get { return false; } }
            public bool HandbrakeHeld { get { return false; } }
        }

        private struct Snap
        {
            public float z, x, speed, elapsed, boostLeft;
            public int crossings;
        }

        private static readonly StringBuilder Sb = new StringBuilder();
        private static int _fails;
        private static int _phase;
        private static double _t0;
        private static GameDirector _dir;
        private static CarController _car;
        private static InputRouter _router;
        private static PauseView _view;
        private static FakeKeys _keys;
        private static Snap _s0;
        private static int _frame0;
        private static bool _gateOpened, _gateClosed;
        private static float _elapsedAtResume;
        private static double _resumeWall;

        public static void RunFromCommandLine()
        {
            Sb.Length = 0; _fails = 0; _phase = 0;
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Check(string what, bool ok)
        {
            Sb.AppendLine("  " + (ok ? "PASS " : "**FAIL** ") + what);
            if (!ok) _fails++;
        }

        private static Snap Take()
        {
            Snap s;
            s.z = _car.Sim.Z; s.x = _car.Sim.X; s.speed = _car.Sim.Speed;
            s.elapsed = _dir.Session.ElapsedSeconds;
            s.boostLeft = _car.Sim.BoostTimeLeft;
            s.crossings = _dir.Session.Sectors.CrossCount;
            return s;
        }

        private static double Now() { return EditorApplication.timeSinceStartup; }

        private static Vector2 ScreenCentre(RectTransform r)
        {
            Canvas c = r.GetComponentInParent<Canvas>().rootCanvas;
            Camera cam = c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera;
            return RectTransformUtility.WorldToScreenPoint(cam, r.position);
        }

        private static string RoleAt(Vector2 p)
        {
            MethodInfo m = typeof(TouchDriveInput).GetMethod("Classify", BindingFlags.NonPublic | BindingFlags.Instance);
            return m.Invoke(_router.Touch, new object[] { p }).ToString();
        }

        private static void Finish()
        {
            EditorApplication.update -= Tick;
            Sb.AppendLine(_fails == 0 ? "[pause probe] ALL PASS" : "[pause probe] " + _fails + " check(s) **FAIL**ed");
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/pause-probe.txt", Sb.ToString());
            Console.WriteLine(Sb.ToString());
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        private static void Tick()
        {
            try { Step(); }
            catch (Exception e)
            {
                Sb.AppendLine("**EXCEPTION** " + e);
                _fails++;
                Finish();
            }
        }

        private static void Step()
        {
            if (_start == 0) _start = Now();
            if (Now() - _start > 150)
            {
                Sb.AppendLine("**TIMEOUT** phase " + _phase + " state " + (_dir != null ? _dir.State.ToString() : "no director")
                              + " frame " + Time.frameCount + " playing " + EditorApplication.isPlaying);
                _fails++; Finish(); return;
            }
            if (!EditorApplication.isPlaying) return;
            if (_phase == 0)
            {
                _dir = UnityEngine.Object.FindAnyObjectByType<GameDirector>();
                _car = UnityEngine.Object.FindAnyObjectByType<CarController>();
                _router = UnityEngine.Object.FindAnyObjectByType<InputRouter>();
                _view = UnityEngine.Object.FindAnyObjectByType<PauseView>();
                if (_dir == null || _car == null || _car.Sim == null || _router == null || _view == null
                    || Time.frameCount < 20)
                {
                    if (Time.frameCount % 120 == 0)
                        Debug.Log("[pause probe] waiting: dir " + (_dir != null) + " car " + (_car != null) + " sim " + (_car != null && _car.Sim != null)
                                  + " router " + (_router != null) + " view " + (_view != null) + " frame " + Time.frameCount);
                    return;
                }
                Sb.AppendLine("================= PAUSE PROBE (play mode, Main.unity) =================");
                _keys = new FakeKeys();
                typeof(InputRouter).GetField("_keyboard", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_router, _keys);

                // Ready: pausing is refused, the button is hidden.
                _dir.SetUserPaused(true);
                Check("Ready: SetUserPaused(true) is refused", !_dir.UserPaused && _dir.State == GameState.Ready);
                Check("Ready: pause button hidden", !_view.ButtonVisible);
                _dir.StartFromReady();
                _phase = 1;
                return;
            }
            if (_phase == 1)   // countdown -> racing
            {
                if (_dir.State == GameState.Countdown)
                {
                    // Pause in the countdown: the lamps hold.
                    if (_t0 == 0)
                    {
                        if (!_view.ButtonVisible) return;
                        _t0 = Now();
                        Check("Countdown: pause button visible", _view.ButtonVisible);
                        _dir.SetUserPaused(true);
                        _lampsAtPause = _dir.LampsLit;
                        Check("Countdown: pausable", _dir.UserPaused);
                    }
                    if (_dir.UserPaused)
                    {
                        if (Now() - _t0 < 1.6) return;
                        Check("Countdown: stays in Countdown, lamps unchanged while paused (" + _lampsAtPause + ")",
                              _dir.State == GameState.Countdown && _dir.LampsLit == _lampsAtPause);
                        _dir.SetUserPaused(false);
                    }
                    return;
                }
                if (_dir.State != GameState.Racing) return;
                _t0 = Now(); _phase = 2;
                _car.Sim.TriggerBoost(0.2f, 0.2f, 6f, 3f);
                return;
            }
            if (_phase == 2)   // run, steering key held, then pause
            {
                if (Now() - _t0 < 1.2) return;
                _keys.Steer = 1f;
                if (Now() - _t0 < 1.5) return;
                Check("Racing: pause button visible", _view.ButtonVisible);
                Check("Racing: steering key reaches the car before the pause", _router.SteerAxis == 1f);

                // Touch classification: the pause button belongs to the HUD, never a steer zone.
                Vector2 p = ScreenCentre(_view.ButtonRect);
                Check("touch on the pause button: HudControls says interactive", HudControls.IsOverInteractive(p));
                Check("touch on the pause button classifies as Role.None (was " + RoleAt(p) + ")", RoleAt(p) == "None");

                _dir.SetUserPaused(true);
                Check("SetUserPaused(true) in Racing", _dir.UserPaused && _car.Paused && _router.Suspended);
                _s0 = Take();
                _frame0 = Time.frameCount;
                Check("a boost timer is running at the pause (" + _s0.boostLeft.ToString("F2") + "s left)", _s0.boostLeft > 0f);
                _t0 = Now(); _phase = 3;
                return;
            }
            if (_phase == 3)   // paused: wait, with a gate cycle in the middle
            {
                double dt = Now() - _t0;
                if (dt > 0.8 && !_gateOpened)
                {
                    _gateOpened = true;
                    _dir.SetGateHeld(true);
                    Check("gate opens while paused: still user-paused and held", _dir.UserPaused && _dir.Held && _car.Paused);
                }
                if (dt > 1.4 && _gateOpened && !_gateClosed)
                {
                    _gateClosed = true;
                    _dir.SetGateHeld(false);
                    Check("gate closes while paused: STILL user-paused, car and input still held",
                          _dir.UserPaused && _car.Paused && _router.Suspended && _view.OverlayVisible == _dir.UserPaused);
                }
                if (dt < 2.5) return;

                Snap s = Take();
                int frames = Time.frameCount - _frame0;
                Check("paused for " + frames + " frames / " + dt.ToString("F1") + "s of real time", frames >= 30);
                Check("car Z unchanged (" + _s0.z.ToString("F3") + ")", s.z == _s0.z);
                Check("car X unchanged", s.x == _s0.x);
                Check("car speed unchanged (" + _s0.speed.ToString("F2") + ")", s.speed == _s0.speed);
                Check("race time unchanged (" + _s0.elapsed.ToString("F3") + "s) - sector clock rides on it", s.elapsed == _s0.elapsed);
                Check("sector crossings unchanged", s.crossings == _s0.crossings);
                Check("boost timer unchanged (" + _s0.boostLeft.ToString("F3") + ")", s.boostLeft == _s0.boostLeft);
                Check("overlay visible, button hidden", _view.OverlayVisible && !_view.ButtonVisible);
                Check("steer output is zero while held", _router.SteerAxis == 0f);
                var audio = UnityEngine.Object.FindAnyObjectByType<RenaultRacer.Audio.AudioDirector>();
                if (audio != null && audio.Unlocked)
                    Check("audio loops ducked (pause gain " + audio.PauseGain.ToString("F2") + ")", audio.PauseGain < 0.05f);
                else Sb.AppendLine("  (audio not unlocked in the editor: duck check skipped)");

                Vector2 rp = ScreenCentre(_view.ResumeRect);
                Check("touch on NADALJUJ classifies as Role.None (was " + RoleAt(rp) + ")", RoleAt(rp) == "None");
                Check("touch on NADALJUJ is interactive", HudControls.IsOverInteractive(rp));

                _dir.SetUserPaused(false);
                _resumeWall = Now();
                _elapsedAtResume = _dir.Session.ElapsedSeconds;
                _t0 = Now(); _phase = 4;
                return;
            }
            if (_phase == 4)   // resumed with the key still held
            {
                if (Now() - _t0 < 0.4) return;
                Check("resumed: not paused, overlay gone", !_dir.UserPaused && !_car.Paused && !_view.OverlayVisible);
                Check("resumed with the key still held: steer is ignored (AwaitingRelease)", _router.SteerAxis == 0f && _router.AwaitingRelease);
                float advanced = _dir.Session.ElapsedSeconds - _elapsedAtResume;
                double wall = Now() - _resumeWall;
                Check("no time jump on resume: race clock advanced " + advanced.ToString("F2") + "s in " + wall.ToString("F2") + "s of wall time",
                      advanced > 0f && advanced <= wall + 0.1f);
                float dz = _car.Sim.Z - _s0.z;
                float lo = Mathf.Min(_s0.speed, _car.Sim.Speed), hi = Mathf.Max(_s0.speed, _car.Sim.Speed);
                Check("position continuous across the pause: moved " + dz.ToString("F0") + " units in " + advanced.ToString("F2")
                      + "s at " + _s0.speed.ToString("F0") + ".." + _car.Sim.Speed.ToString("F0") + " u/s",
                      dz >= lo * advanced * 0.9f && dz <= hi * advanced * 1.1f);
                _keys.Steer = 0f;
                _t0 = Now(); _phase = 5;
                return;
            }
            if (_phase == 5)   // released, then a fresh press steers again
            {
                if (Now() - _t0 < 0.2) return;
                Check("key released: awaiting-release cleared", !_router.AwaitingRelease);
                _keys.Steer = 1f;
                _t0 = Now(); _phase = 6;
                return;
            }
            if (_phase == 6)
            {
                if (Now() - _t0 < 0.2) return;
                Check("fresh press after the release steers", _router.SteerAxis == 1f);
                _keys.Steer = 0f;

                // Esc-style toggle path and restart clearing the pause.
                _dir.SetUserPaused(true);
                _dir.Restart();
                Check("restart clears a user pause", !_dir.UserPaused && !_car.Paused);
                Check("restart returns to the countdown", _dir.State == GameState.Countdown);
                Finish();
            }
        }

        private static int _lampsAtPause;
        private static double _start;
    }
}
