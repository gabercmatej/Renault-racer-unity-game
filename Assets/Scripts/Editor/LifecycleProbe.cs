using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using RenaultRacer.Audio;
using RenaultRacer.Core;
using RenaultRacer.InputSystem;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.UI;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Play-mode verification of the hold / finish lifecycle (commit 7683729), on the REAL
    /// Main scene: GameDirector, CarController, InputRouter, RotateGate, PauseView, AudioDirector.
    ///   -executeMethod RenaultRacer.EditorTools.LifecycleProbe.RunFromCommandLine   (no -quit)
    /// Report: Logs/lifecycle-probe.txt, pass marker "[lifecycle probe] ALL PASS".
    ///
    /// The portrait gate is driven through the real RotateGate (RotateGate.ForcePortrait, an
    /// editor-only hook), page hides through AudioUnlockBridge.SimulatedHideCount. Near-line
    /// finishes teleport the car and sync RaceSession's private bookkeeping by reflection so the
    /// REAL finish path (RaceSession.Tick -> Validate -> Finished -> TickPostFinish) runs.
    /// A tap cannot be injected in batch, so "tap while gated is ignored" is checked as "the
    /// restart-settle timer does not advance and the state does not change".
    /// </summary>
    public static class LifecycleProbe
    {
        private sealed class FakeKeys : IDriveInput
        {
            public float Steer;
            public float SteerAxis { get { return Steer; } }
            public bool BrakeHeld { get { return false; } }
            public bool HandbrakeHeld { get { return false; } }
        }

        private struct Snap
        {
            public float z, x, speed, elapsed, boostLevel, boostLeft;
            public int crossings, sectorsDone, taken, collected;
            public float postFinish;
        }

        private static readonly StringBuilder Sb = new StringBuilder();
        private static int _fails;
        private static GameDirector _dir;
        private static CarController _car;
        private static InputRouter _router;
        private static PauseView _view;
        private static RotateGate _gate;
        private static AudioDirector _audio;
        private static TrackRuntime _track;
        private static FakeKeys _keys;
        private static double _start;
        private static float _step, _maxStepsTime, _maxSpeed;
        private static int _finishedLogs, _settleLogs, _rejectLogs;
        private static readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
        private static bool _done;
        private static bool _timedOut;

        public static void RunFromCommandLine()
        {
            Sb.Length = 0; _fails = 0; _done = false; _start = 0; Stack.Clear();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            Stack.Push(Script());
            EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }

        private static void Check(string what, bool ok)
        {
            Sb.AppendLine("  " + (ok ? "PASS " : "**FAIL** ") + what);
            if (!ok) _fails++;
        }

        private static void Note(string s) { Sb.AppendLine("  " + s); }
        private static void Head(string s) { Sb.AppendLine(); Sb.AppendLine("--- " + s); }

        private static double Now() { return EditorApplication.timeSinceStartup; }

        private static void OnLog(string msg, string stack, LogType t)
        {
            if (msg.Contains("[GameDirector] FINISHED")) _finishedLogs++;
            else if (msg.Contains("[GameDirector] LAP REJECTED")) _rejectLogs++;
            else if (msg.Contains("[GameDirector] finish settle")) _settleLogs++;
        }

        private static void End()
        {
            if (_done) return;
            _done = true;
            RotateGate.ForcePortrait = false;
            Application.logMessageReceived -= OnLog;
            EditorApplication.update -= Tick;
            Sb.AppendLine(_fails == 0 ? "[lifecycle probe] ALL PASS" : "[lifecycle probe] " + _fails + " check(s) **FAIL**ed");
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/lifecycle-probe.txt", Sb.ToString());
            Console.WriteLine(Sb.ToString());
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        private static void Tick()
        {
            try
            {
                if (_start == 0) _start = Now();
                if (Now() - _start > 420)
                {
                    Sb.AppendLine("**TIMEOUT** state " + (_dir != null ? _dir.State.ToString() : "no director") + " frame " + Time.frameCount);
                    _fails++; End(); return;
                }
                if (!EditorApplication.isPlaying) return;
                if (Stack.Count == 0) { End(); return; }
                IEnumerator top = Stack.Peek();
                if (!top.MoveNext()) { Stack.Pop(); return; }
                IEnumerator nested = top.Current as IEnumerator;
                if (nested != null) Stack.Push(nested);
            }
            catch (Exception e)
            {
                Sb.AppendLine("**EXCEPTION** " + e);
                _fails++; End();
            }
        }

        // ---- waiting helpers ----------------------------------------------------------
        private static IEnumerator Frames(int n)
        {
            int target = Time.frameCount + n;
            while (Time.frameCount < target) yield return null;
        }

        private static IEnumerator Secs(double s)
        {
            double t = Now() + s;
            while (Now() < t) yield return null;
        }

        private static IEnumerator Until(Func<bool> cond, double timeout)
        {
            double t = Now() + timeout;
            _timedOut = false;
            while (!cond())
            {
                if (Now() > t) { _timedOut = true; yield break; }
                yield return null;
            }
        }

        // ---- state helpers ------------------------------------------------------------
        private static Snap Take()
        {
            Snap s;
            s.z = _car.Sim.Z; s.x = _car.Sim.X; s.speed = _car.Sim.Speed;
            s.elapsed = _dir.Session.ElapsedSeconds;
            s.boostLevel = _car.Sim.BoostLevel; s.boostLeft = _car.Sim.BoostTimeLeft;
            s.crossings = _dir.Session.Sectors.CrossCount;
            s.sectorsDone = _dir.Session.SectorsCompleted;
            s.taken = _car.Pickups != null && _car.Pickups.Set != null ? _car.Pickups.Set.TakenCount : -1;
            s.collected = _car.BoostPickupsCollected;
            s.postFinish = _dir.PostFinishElapsed;
            return s;
        }

        private static bool Same(Snap a, Snap b)
        {
            return a.z == b.z && a.x == b.x && a.speed == b.speed && a.elapsed == b.elapsed
                && a.boostLevel == b.boostLevel && a.boostLeft == b.boostLeft
                && a.crossings == b.crossings && a.sectorsDone == b.sectorsDone
                && a.taken == b.taken && a.collected == b.collected && a.postFinish == b.postFinish;
        }

        private static float RestEnded()
        {
            return (float)typeof(GameDirector).GetField("_restEnded", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_dir);
        }

        private static float CountdownTimer()
        {
            return (float)typeof(GameDirector).GetField("_countdownTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_dir);
        }

        private static IEnumerator GateUp()
        {
            RotateGate.ForcePortrait = true;
            if (_gate != null)
            {
                yield return Until(() => _gate.IsGated && _dir.GateHeld, 3);
                Check("gate went up (real RotateGate, debounce)", !_timedOut);
            }
            else _dir.SetGateHeld(true);
            yield return null;
        }

        private static IEnumerator GateDown()
        {
            RotateGate.ForcePortrait = false;
            if (_gate != null)
            {
                yield return Until(() => !_gate.IsGated && !_dir.GateHeld, 3);
                Check("gate went down", !_timedOut);
            }
            else _dir.SetGateHeld(false);
        }

        private static IEnumerator ToRacing(float minSpeedFrac)
        {
            if (_dir.State != GameState.Countdown) _dir.Restart();
            yield return Until(() => _dir.State == GameState.Racing, 15);
            Check("reached Racing", !_timedOut);
            if (minSpeedFrac > 0f)
            {
                yield return Until(() => _car.Sim.Speed > _maxSpeed * minSpeedFrac, 20);
                Check("car at speed (" + _car.Sim.Speed.ToString("F0") + " u/s)", !_timedOut);
            }
        }

        /// <summary>Put the car just before the line and sync the session so the real finish path validates.</summary>
        private static void Teleport(float unitsBeforeLine)
        {
            float len = _track.Track.Length;
            float z = len - unitsBeforeLine;
            _car.Sim.Z = z;
            _car.Sim.X = 0f;
            _car.Sim.Speed = _maxSpeed * 0.9f;
            RaceSession sess = _dir.Session;
            Type st = typeof(RaceSession);
            st.GetField("_lastZ", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(sess, z);
            st.GetProperty("CheckpointsPassed").GetSetMethod(true).Invoke(sess, new object[] { sess.CheckpointCount });
            st.GetProperty("ElapsedSeconds").GetSetMethod(true).Invoke(sess, new object[] { len / _maxSpeed + 5f });
        }

        private static IEnumerator Script()
        {
            Application.logMessageReceived += OnLog;
            while (_dir == null || _car == null || _car.Sim == null || _router == null || _view == null || _track == null
                   || _track.Track == null || _dir.Session == null || Time.frameCount < 20)
            {
                _dir = UnityEngine.Object.FindAnyObjectByType<GameDirector>();
                _car = UnityEngine.Object.FindAnyObjectByType<CarController>();
                _router = UnityEngine.Object.FindAnyObjectByType<InputRouter>();
                _view = UnityEngine.Object.FindAnyObjectByType<PauseView>();
                _track = UnityEngine.Object.FindAnyObjectByType<TrackRuntime>();
                yield return null;
            }
            _gate = UnityEngine.Object.FindAnyObjectByType<RotateGate>();
            _audio = UnityEngine.Object.FindAnyObjectByType<AudioDirector>();
            _keys = new FakeKeys();
            typeof(InputRouter).GetField("_keyboard", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_router, _keys);
            _step = _car.StepDuration;
            _maxStepsTime = 4f * _step;
            _maxSpeed = _car.CurrentProfile.maxSpeed;
            Sb.AppendLine("================= LIFECYCLE PROBE (play mode, Main.unity) =================");
            Sb.AppendLine("  step " + _step.ToString("F4") + "s  maxSpeed " + _maxSpeed.ToString("F0") + "  track " + _track.Track.Length.ToString("F0")
                          + "  real RotateGate " + (_gate != null) + "  audio " + (_audio != null));

            // ---- 5a: page hide in Ready does not pause ----------------------------------
            Head("5a. page hide in Ready");
            yield return Until(() => _dir.State == GameState.Ready, 10);
            AudioUnlockBridge.SimulatedHideCount++;
            yield return Frames(3);
            Check("Ready + page hide: no pause", !_dir.UserPaused && _dir.State == GameState.Ready);
            _dir.StartFromReady();
            yield return null;

            // ---- 4: gate during the countdown -------------------------------------------
            Head("4. gate during the countdown");
            yield return Until(() => _dir.State == GameState.Countdown && _dir.LampsLit >= 1, 10);
            yield return Frames(2);
            yield return GateUp();
            float ctUp = CountdownTimer(); int lampsUp = _dir.LampsLit;
            double wall0 = Now();
            yield return Secs(2.4);
            float ctHeld = CountdownTimer();
            Check("countdown timer frozen behind the gate (" + ctUp.ToString("F3") + " -> " + ctHeld.ToString("F3") + " over "
                  + (Now() - wall0).ToString("F1") + "s)", ctHeld == ctUp);
            Check("lamps frozen (" + lampsUp + ")", _dir.LampsLit == lampsUp && _dir.State == GameState.Countdown);
            Check("still Countdown, not Racing, held", _dir.State == GameState.Countdown && _dir.Held);
            yield return GateDown();
            yield return Frames(1);
            float ctAfter = CountdownTimer();
            Check("countdown continues from the frozen value, not reset or jumped (" + ctUp.ToString("F3") + " -> " + ctAfter.ToString("F3") + ")",
                  ctAfter >= ctUp && ctAfter <= ctUp + 0.5f && _dir.LampsLit >= lampsUp);
            double tGo = Now();
            int lampsMin = _dir.LampsLit; bool lampsMonotone = true;
            yield return Until(() =>
            {
                if (_dir.LampsLit < lampsMin) lampsMonotone = false;
                lampsMin = Mathf.Max(lampsMin, _dir.LampsLit);
                return _dir.State == GameState.Racing;
            }, 10);
            Check("goes green after the gate: Racing in " + (Now() - tGo).ToString("F2") + "s (timer says ~" + (4f - ctAfter).ToString("F2") + "s left)",
                  !_timedOut && Mathf.Abs((float)(Now() - tGo) - (4f - ctAfter)) < 0.8f);
            Check("lamps never went back down", lampsMonotone);
            Check("race clock starts near 0 at green (" + _dir.Session.ElapsedSeconds.ToString("F2") + ")", _dir.Session.ElapsedSeconds < 0.5f);

            // ---- 5b: page hide in countdown ---------------------------------------------
            Head("5b. page hide in the countdown");
            _dir.Restart();
            yield return Frames(3);
            Check("restart -> Countdown", _dir.State == GameState.Countdown);
            AudioUnlockBridge.SimulatedHideCount++;
            yield return Frames(3);
            Check("Countdown + page hide: UserPaused, car paused, overlay", _dir.UserPaused && _car.Paused && _view.OverlayVisible);
            _dir.SetUserPaused(false);

            // ---- 1: gate mid-race -------------------------------------------------------
            Head("1. gate up mid-race");
            yield return ToRacing(0.3f);
            _car.Sim.TriggerBoost(0.2f, 0.2f, 6f, 3f);
            _keys.Steer = 1f;
            yield return Secs(0.5);
            Check("steer key reaches the car before the gate", _router.SteerAxis == 1f);
            yield return GateUp();
            Check("Held, car paused, input suspended", _dir.Held && _dir.GateHeld && !_dir.UserPaused && _car.Paused && _router.Suspended);
            Check("pause not offered while gated", !_dir.CanUserPause);
            _dir.SetUserPaused(true);
            Check("SetUserPaused(true) refused behind the gate", !_dir.UserPaused);
            yield return Frames(1);
            Snap g0 = Take(); int f0 = Time.frameCount; double w0 = Now();
            Note("snapshot: Z " + g0.z.ToString("F1") + " X " + g0.x.ToString("F3") + " speed " + g0.speed.ToString("F1") + " elapsed " + g0.elapsed.ToString("F3")
                 + " boost " + g0.boostLevel.ToString("F3") + "/" + g0.boostLeft.ToString("F2") + "s crossings " + g0.crossings + " taken " + g0.taken + " collected " + g0.collected);
            Check("a boost is running at the gate (" + g0.boostLeft.ToString("F2") + "s left)", g0.boostLeft > 0f);
            yield return Secs(1.6);
            yield return Frames(30);
            Snap g1 = Take();
            Check("over " + (Time.frameCount - f0) + " frames / " + (Now() - w0).ToString("F1") + "s: Z, X, speed, elapsed, sector, boost level+timer, pickups all bit-identical", Same(g0, g1));
            Check("steer output zero, input suspended while the key is held", _router.SteerAxis == 0f && _router.Suspended);
            if (_audio != null && _audio.Unlocked)
                Check("loops ducked behind the gate (PauseGain " + _audio.PauseGain.ToString("F3") + ")", _audio.PauseGain < 0.01f);
            else Note("(audio not unlocked: duck check skipped)");

            // ---- 2: gate down -----------------------------------------------------------
            Head("2. gate down - resume (steer key still held through the gate)");
            float wallGated = (float)(Now() - w0);
            yield return GateDown();
            yield return Frames(1);
            Snap r1 = Take();
            float dE = r1.elapsed - g0.elapsed, dZ = r1.z - g0.z;
            Check("first frame after the gate: elapsed advanced " + dE.ToString("F4") + "s (<= maxSteps " + _maxStepsTime.ToString("F4") + "; gated wall was " + wallGated.ToString("F1") + "s)",
                  dE >= 0f && dE <= _maxStepsTime + 1e-4f);
            Check("first frame after the gate: Z moved " + dZ.ToString("F1") + " (<= " + (_maxSpeed * 1.5f * _maxStepsTime).ToString("F1") + ")",
                  dZ >= 0f && dZ <= _maxSpeed * 1.5f * _maxStepsTime);
            Check("resumed: not held, car running, input live", !_dir.Held && !_car.Paused && !_router.Suspended);
            Check("key held through the gate: steer ignored, AwaitingRelease", _router.SteerAxis == 0f && _router.AwaitingRelease);
            Check("boost state continues, not reset (" + r1.boostLeft.ToString("F2") + "s left)", r1.boostLeft > 0f && r1.boostLeft <= g0.boostLeft);
            yield return Secs(0.5);
            Snap r2 = Take();
            Check("race clock runs normally afterwards (+" + (r2.elapsed - r1.elapsed).ToString("F2") + "s in ~0.5s)", r2.elapsed - r1.elapsed > 0.2f && r2.elapsed - r1.elapsed < 0.9f);
            Check("still ignoring the held key", _router.SteerAxis == 0f);
            _keys.Steer = 0f;
            yield return Frames(3);
            Check("released: AwaitingRelease cleared", !_router.AwaitingRelease);
            _keys.Steer = -1f;
            yield return Frames(3);
            Check("fresh press steers again", _router.SteerAxis == -1f);
            _keys.Steer = 0f;
            if (_audio != null && _audio.Unlocked)
            {
                yield return Secs(0.6);
                Check("loops back after the gate (PauseGain " + _audio.PauseGain.ToString("F2") + ")", _audio.PauseGain > 0.99f);
            }

            // ---- 5c: page hide while racing ---------------------------------------------
            Head("5c. page hide while racing");
            Check("racing, not paused before the hide", _dir.State == GameState.Racing && !_dir.UserPaused);
            AudioUnlockBridge.SimulatedHideCount++;
            yield return Frames(3);
            Check("Racing + page hide: UserPaused, car paused, input suspended, PREMOR overlay", _dir.UserPaused && _car.Paused && _view.OverlayVisible && _router.Suspended);
            AudioUnlockBridge.SimulatedHideCount++;
            yield return Frames(3);
            Check("a second hide while paused keeps the pause (no toggle off)", _dir.UserPaused);

            // ---- 3: user pause -> gate up -> gate down ----------------------------------
            Head("3. user pause -> gate up -> gate down");
            Snap p0 = Take();
            yield return GateUp();
            Check("gate up over a user pause: both held", _dir.UserPaused && _dir.GateHeld);
            yield return Secs(0.6);
            yield return GateDown();
            yield return Frames(3);
            Check("gate down: STILL UserPaused, car paused, input suspended, PREMOR overlay visible",
                  _dir.UserPaused && _dir.Held && _car.Paused && _router.Suspended && _view.OverlayVisible);
            yield return Frames(30);
            Check("nothing moved across pause + gate + 30 frames", Same(p0, Take()));
            _dir.SetUserPaused(false);
            yield return Frames(3);
            Check("user resume works: running, overlay gone, input live", !_dir.Held && !_car.Paused && !_view.OverlayVisible && !_router.Suspended);
            yield return Secs(0.3);
            Check("clock advancing after the resume", _dir.Session.ElapsedSeconds > p0.elapsed + 0.1f);

            // ---- 6 / 7 / 8: finish ------------------------------------------------------
            yield return FinishRun("6. finish A (plain)", false);
            yield return FinishRun("7. finish B (gate in the window, gate after the settle)", true);

            Head("8. restart after Settled");
            Check("(precondition) settled and paused", _dir.Settled && _car.Paused);
            _dir.Restart();
            yield return Frames(2);
            Check("Settled cleared", !_dir.Settled);
            Check("car not paused", !_car.Paused);
            Check("FinishDecel 0 (" + _car.Sim.FinishDecel + ")", _car.Sim.FinishDecel == 0f);
            Check("PostFinishElapsed 0", _dir.PostFinishElapsed == 0f);
            Check("countdown starts cleanly: Countdown, 0 lamps, car at Z 0 speed 0, clock 0, pickups 0",
                  _dir.State == GameState.Countdown && _dir.LampsLit == 0 && _car.Sim.Z == 0f && _car.Sim.Speed == 0f
                  && _dir.Session.ElapsedSeconds == 0f && _car.BoostPickupsCollected == 0);
            if (_audio != null && _audio.Unlocked)
            {
                yield return Secs(1.0);
                Check("loops fade back in after restart (PauseGain " + _audio.PauseGain.ToString("F2") + ")", _audio.PauseGain > 0.99f);
            }
            yield return FinishRun("8b. second finish after a restart settles again", false);

            Head("done");
        }

        private static IEnumerator FinishRun(string title, bool withGate)
        {
            Head(title);
            _finishedLogs = 0; _settleLogs = 0; _rejectLogs = 0;
            yield return ToRacing(0f);
            yield return Secs(0.8);
            Teleport(900f);
            Note("teleported to " + (_track.Track.Length - 900f).ToString("F0") + ", speed " + _car.Sim.Speed.ToString("F0") + ", elapsed " + _dir.Session.ElapsedSeconds.ToString("F1"));

            float prevElapsed = _dir.Session.ElapsedSeconds, prevZ = _car.Sim.Z;
            double tmax = Now() + 20;
            while (_dir.State == GameState.Racing && Now() < tmax)
            {
                prevElapsed = _dir.Session.ElapsedSeconds; prevZ = _car.Sim.Z;
                yield return null;
            }
            float len = _track.Track.Length;
            Check("crossed the line into Finished (state " + _dir.State + ", rejection " + _dir.Rejection + ")", _dir.State == GameState.Finished && _dir.Session.Finished);
            if (_dir.State != GameState.Finished) yield break;

            float eFin = _dir.Session.ElapsedSeconds;
            int collectedFin = _car.BoostPickupsCollected;
            float speedFin = _car.Sim.Speed;
            Check("crossing: Z " + _car.Sim.Z.ToString("F0") + " >= line " + len.ToString("F0") + ", previous sample Z " + prevZ.ToString("F0") + " < line",
                  _car.Sim.Z >= len && prevZ < len);
            Check("crossing frame advanced the clock " + (eFin - prevElapsed).ToString("F4") + "s (within maxSteps)", eFin - prevElapsed >= 0f && eFin - prevElapsed <= _maxStepsTime + 1e-4f);
            SectorTiming sec = _dir.Session.Sectors;
            int lastSector = sec.SectorCount - 1;
            if (sec.Completed(lastSector))
                Check("finish-line sector gate closed on the SAME tick/clock: ClosedAt " + sec.ClosedAt(lastSector).ToString("F4") + " == ElapsedSeconds " + eFin.ToString("F4"),
                      sec.ClosedAt(lastSector) == eFin);
            else Note("(finish sector gate not completed - the teleport skipped the earlier gates)");
            Check("FinishDecel set (" + _car.Sim.FinishDecel + "), throttle and controls off",
                  _car.Sim.FinishDecel == GameDirector.FinishDecel && !_car.Sim.ThrottleEnabled && !_car.Sim.ControlsEnabled);
            Note("speed at the line " + speedFin.ToString("F0") + " (" + (speedFin / _maxSpeed).ToString("P0") + " of top)");

            AudioUnlockBridge.SimulatedHideCount++;
            yield return Frames(3);
            Check("Finished + page hide: no pause", !_dir.UserPaused);

            bool elapsedFrozen = true, stateStable = true, speedMonotone = true, collectedSame = true;
            float lastSpeed = _car.Sim.Speed; int ticks = 0;
            int gateStage = 0;
            tmax = Now() + 25;
            while (!_dir.Settled && Now() < tmax)
            {
                if (_dir.Session.ElapsedSeconds != eFin) elapsedFrozen = false;
                if (_dir.State != GameState.Finished) stateStable = false;
                if (_car.Sim.Speed > lastSpeed + 1e-3f) speedMonotone = false;
                lastSpeed = _car.Sim.Speed;
                if (_car.BoostPickupsCollected != collectedFin) collectedSame = false;
                ticks++;

                if (withGate && gateStage == 0 && _dir.PostFinishElapsed > 1.0f)
                {
                    gateStage = 1;
                    yield return GateUp();
                    Snap gs = Take(); float gateRest = RestEnded(); double gateWall = Now();
                    Note("gate up in the finish window: PostFinishElapsed " + gs.postFinish.ToString("F3") + ", restEnded " + gateRest.ToString("F3")
                         + ", speed " + gs.speed.ToString("F1"));
                    Check("gate holds the car during the window (Paused, Held, not Settled)", _car.Paused && _dir.Held && !_dir.Settled);
                    yield return Secs(3.6);   // longer than the whole 3 s window
                    Snap g = Take();
                    Check("window frozen over " + (Now() - gateWall).ToString("F1") + "s of gate: PostFinishElapsed " + gs.postFinish.ToString("F3") + " -> " + g.postFinish.ToString("F3"),
                          g.postFinish == gs.postFinish);
                    Check("Z, speed, elapsed constant behind the gate; not Settled although > 3 s of wall time passed",
                          g.z == gs.z && g.speed == gs.speed && g.elapsed == gs.elapsed && !_dir.Settled);
                    float rest = RestEnded();
                    Check("restart-tap settle timer does not advance behind the gate (" + gateRest.ToString("F3") + " -> " + rest.ToString("F3") + "); still Finished, no restart",
                          rest == gateRest && _dir.State == GameState.Finished);
                    yield return GateDown();
                    yield return Frames(1);
                    float postAfter = _dir.PostFinishElapsed;
                    Check("gate down: window continues " + gs.postFinish.ToString("F3") + " -> " + postAfter.ToString("F3") + " (one frame <= maxSteps)",
                          postAfter >= gs.postFinish && postAfter <= gs.postFinish + _maxStepsTime + 1e-4f);
                    Check("gate down: car running again (not Paused, not Held)", !_car.Paused && !_dir.Held);
                    gateStage = 2;
                    lastSpeed = _car.Sim.Speed;
                    continue;
                }
                yield return null;
            }
            Check("Finished stable; ElapsedSeconds never changed after the line (" + eFin.ToString("F4") + "s) over " + ticks + " ticks", elapsedFrozen && stateStable);
            Check("speed decays monotonically after the line", speedMonotone);
            Check("BoostPickupsCollected unchanged after the line (" + collectedFin + ")", collectedSame);
            Check("Settled reached", _dir.Settled);
            if (!_dir.Settled) yield break;
            float post = _dir.PostFinishElapsed;
            Check("settled at PostFinishElapsed " + post.ToString("F4") + " (window " + GameDirector.PostFinishSeconds + "s, + at most one frame of steps)",
                  post >= GameDirector.PostFinishSeconds - 1e-4f && post <= GameDirector.PostFinishSeconds + _maxStepsTime + 1e-4f);
            Check("car.Paused after settle", _car.Paused);
            Check("speed at settle " + _car.Sim.Speed.ToString("F1") + " <= 10% of top (from " + speedFin.ToString("F0") + " at the line)", _car.Sim.Speed <= 0.1f * _maxSpeed);
            Check("FINISHED logged once (" + _finishedLogs + "), LAP REJECTED " + _rejectLogs + ", settle logged once (" + _settleLogs + ")",
                  _finishedLogs == 1 && _rejectLogs == 0 && _settleLogs == 1);
            Snap s0 = Take();
            yield return Frames(60);
            yield return Secs(0.8);
            Snap s1 = Take();
            Check("60+ frames after the settle: Z " + s0.z.ToString("F1") + ", speed " + s0.speed.ToString("F1") + ", elapsed, window counter all constant", Same(s0, s1));
            Check("still Finished, settle did not fire twice (" + _settleLogs + ")", _dir.State == GameState.Finished && _settleLogs == 1 && _dir.Settled);
            if (_audio != null && _audio.Unlocked)
            {
                yield return Secs(0.8);
                Check("loops faded out on the settle (PauseGain " + _audio.PauseGain.ToString("F3") + ")", _audio.PauseGain < 0.01f);
            }
            if (withGate)
            {
                yield return GateUp();
                yield return Secs(0.4);
                yield return GateDown();
                yield return Frames(3);
                Check("gate cycle over a settled car: car STILL paused (settle survives ApplyHold), Settled, nothing moved",
                      _car.Paused && _dir.Settled && !_dir.Held && Same(s0, Take()) && _settleLogs == 1);
            }
        }
    }
}
