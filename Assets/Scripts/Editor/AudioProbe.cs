using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Audio;
using RenaultRacer.UI;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Does the car actually sound ELECTRIC, is it actually quieter, and does it actually
    /// stop stepping? Editor-only, never ships.
    ///
    /// WHY THIS EXISTS. Audio is the one system in this project with no instrument. Every
    /// other claim - lap times, allocation, triangle counts, whether the streaks are on
    /// screen - has a probe that measures it; audio had "it sounds better", written by the
    /// person who changed it. That is exactly the position the drift and the speed lines
    /// were in, and the fix is the same: render the real clips with the real synthesis
    /// code, drive the real runtime mapping, and measure.
    ///
    /// Everything here is CPU-side arithmetic on float arrays, so it answers under
    /// `-batchmode -nographics` and needs no scene, no audio device and no GPU:
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///     -executeMethod RenaultRacer.EditorTools.AudioProbe.RunFromCommandLine -logFile -
    ///
    /// ## What each case actually asks
    ///
    /// [1] IS IT A COMBUSTION ENGINE? A sawtooth - the old waveform, and the shape of an
    ///     exhaust pulse train - has harmonics falling as 1/n and a sharp per-cycle reset.
    ///     So the test is two-sided: the harmonic stack must fall much faster than 1/n,
    ///     AND there must be real energy at frequencies that are not harmonics at all,
    ///     because an inharmonic whine is what a motor has and an engine does not.
    /// [2] IS THE WAVEFORM SMOOTH? The largest single-sample jump in the loop. A sawtooth
    ///     resets by a full amplitude in one sample; that edge is the buzz.
    /// [3] DOES THE LOOP CLICK? The seam step against the largest ordinary step inside the
    ///     clip. The seam is just another sample boundary, so "inaudible" means "no bigger
    ///     than the steps either side of it".
    /// [4] DOES PITCH TRACK SPEED, both ways, and is it strictly monotonic?
    /// [5] CAN THE PITCH STEP? Driven over four speed profiles including a barrier impact
    ///     that removes three quarters of the car's speed in ONE frame.
    /// [6] IS IT QUIETER? Peak and RMS, flat and A-weighted, against the previous
    ///     sawtooth engine rendered by the same code path.
    /// [7] DOES IT DOMINATE? The motor against every cue that has to stay readable over
    ///     it, at four speeds.
    ///
    /// ## Why A-weighting
    ///
    /// Flat RMS is the wrong instrument for "is the motor too loud". The motor's energy
    /// sits at 35-160 Hz where the ear is 15-25 dB less sensitive; the tyre squeal sits at
    /// 2-3 kHz where it is most sensitive. Compared flat, a motor that is perfectly
    /// comfortable looks like it is burying the tyres. So both numbers are reported and
    /// the PASS/FAIL is taken on the A-weighted one, which is the one that corresponds to
    /// what a player hears. The filter is the standard IEC 61672 cascade, normalised so a
    /// 1 kHz sine passes at unity, with the 12.2 kHz pole clamped below Nyquist (the clips
    /// are 22.05 kHz, so there is nothing up there to weight anyway).
    /// </summary>
    public static class AudioProbe
    {
        private const string LogPath = "Logs/audio-probe.txt";
        private const int SR = AudioDirector.SampleRate;
        private const float Step = 1f / 60f;

        // The mix values the SCENE holds for the cues this probe compares against. They
        // are not the component defaults - Main.unity serialises its own - so the level
        // comparison is run against what actually ships. If these and Main.unity ever
        // disagree, case [7] is measuring a mix nobody hears.
        private const float SceneMaster = 0.45f;
        private const float SceneSlip = 0.22f;
        private const float SceneImpact = 0.4f;
        private const float SceneUi = 0.4f;

        [MenuItem("Renault Racer/Audio probe - EV motor (Phase 10d)", false, 48)]
        public static void RunFromMenu() { Debug.Log(Run()); }

        public static void RunFromCommandLine()
        {
            string report;
            try { report = Run(); }
            catch (Exception e)
            {
                Console.WriteLine("[AudioProbe] threw: " + e);
                EditorApplication.Exit(2);
                return;
            }
            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(LogPath, report);
                Console.WriteLine("[AudioProbe] written to " + LogPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            int failures = 0;
            for (int i = report.IndexOf("**FAIL**", StringComparison.Ordinal); i >= 0;
                 i = report.IndexOf("**FAIL**", i + 1, StringComparison.Ordinal)) failures++;
            Console.WriteLine("[AudioProbe] " + (failures == 0 ? "ALL PASS" : failures + " FAILURE(S)"));
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static string Pass(bool ok) { return ok ? "PASS" : "**FAIL**"; }

        // ------------------------------------------------------------------- the run

        private static string Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AUDIO PROBE - EV motor, levels, loop seams");
            sb.AppendLine("==========================================");
            sb.AppendLine("Every clip rendered by AudioDirector's own synthesis code; every");
            sb.AppendLine("mapping driven through AudioDirector's own static functions.");
            sb.AppendLine();

            // Component defaults, read off a real instance whose Awake has NOT run (a
            // component added to an inactive GameObject does not get Awake). This is how
            // the probe uses the shipping tuning values without copying them.
            GameObject holder = new GameObject("AudioProbeDefaults");
            holder.SetActive(false);
            AudioDirector d = holder.AddComponent<AudioDirector>();

            float idle = d.motorPitchIdle, top = d.motorPitchTop;
            float pitchSmooth = d.motorPitchSmoothing, pitchRate = d.motorPitchMaxRate;
            float gainSmooth = d.motorGainSmoothing, gainRate = d.motorGainMaxRate;
            float loadSmooth = d.loadSmoothing;
            float motorVol = d.motorVolume, rushVol = d.rushVolume, master = d.masterVolume, grassVol = d.grassVolume;

            sb.AppendLine("Tuning under test (AudioDirector component defaults):");
            sb.AppendLine("  motorVolume        " + motorVol.ToString("F4") + "   (was engineVolume 0.16 in Main.unity)");
            sb.AppendLine("  rushVolume         " + rushVol.ToString("F4") + "   (new)");
            sb.AppendLine("  masterVolume       " + master.ToString("F4"));
            sb.AppendLine("  pitch idle -> top  " + idle.ToString("F2") + " -> " + top.ToString("F2"));
            sb.AppendLine("  pitch smoothing    " + pitchSmooth.ToString("F1") + "/s, hard slew cap "
                          + pitchRate.ToString("F2") + "/s");
            sb.AppendLine("  gain  smoothing    " + gainSmooth.ToString("F1") + "/s, hard slew cap "
                          + gainRate.ToString("F2") + "/s");
            sb.AppendLine();

            _idle = idle; _top = top;          // the level cases resample at the real rates
            UnityEngine.Object.DestroyImmediate(holder);

            float[] motor = AudioDirector.BuildMotorSamples();
            float[] rush = AudioDirector.BuildRushSamples();
            float[] slip = AudioDirector.BuildSlipSamples();
            float[] gravel = LoadGrass();
            float[] beep = AudioDirector.BuildBeepSamples();
            float[] impact = AudioDirector.BuildImpactSamples();
            float[] finish = AudioDirector.BuildFinishSamples();
            float[] legacy = LegacySawtoothEngine();

            Spectrum(sb, motor, legacy);
            Smoothness(sb, motor, legacy);
            Seams(sb, motor, rush, slip, gravel);
            PitchTracking(sb, idle, top);
            PitchStepping(sb, idle, top, pitchSmooth, pitchRate, gainSmooth, gainRate, loadSmooth);
            Levels(sb, motor, rush, legacy, motorVol, rushVol, master);
            Readability(sb, motor, rush, slip, gravel, beep, impact, finish,
                         motorVol, rushVol, master, grassVol);
            GrassClipChecks(sb, gravel, motor, rush, motorVol, rushVol, master, grassVol);
            Lifecycle(sb);

            return sb.ToString();
        }

        // --------------------------------------------------------------- [1] spectrum

        private static void Spectrum(StringBuilder sb, float[] motor, float[] legacy)
        {
            sb.AppendLine("[1] SPECTRUM - is this a combustion note?");
            sb.AppendLine("    A sawtooth's harmonics fall as 1/n: 1.000, 0.500, 0.333, 0.250.");
            sb.AppendLine("    The loop is exactly one second, so DFT bin k IS k cycles per loop and");
            sb.AppendLine("    a Goertzel at each partial has zero leakage.");
            sb.AppendLine();

            int[] hc = AudioDirector.MotorHarmonicCycles;
            int[] wc = AudioDirector.MotorWhineCycles;
            float h1 = Goertzel(motor, hc[0]);

            sb.AppendLine("    MOTOR, harmonic stack (bin = cycles/loop):");
            for (int i = 0; i < hc.Length; i++)
            {
                float a = Goertzel(motor, hc[i]);
                sb.AppendLine("      " + hc[i].ToString().PadLeft(5) + "  amp " + a.ToString("F5")
                              + "   /H1 " + (a / h1).ToString("F4")
                              + "   sawtooth would be " + (1f / (i + 1f)).ToString("F4"));
            }
            sb.AppendLine("    MOTOR, inharmonic whine (none of these is a multiple of 70):");
            float inharm = 0f;
            for (int i = 0; i < wc.Length; i++)
            {
                float a = Goertzel(motor, wc[i]);
                inharm += a;
                sb.AppendLine("      " + wc[i].ToString().PadLeft(5) + "  amp " + a.ToString("F5")
                              + "   ratio to fundamental " + (wc[i] / (float)hc[0]).ToString("F3"));
            }
            // The PWM sidebands are inharmonic too, and they are the shimmer.
            int[] sidebands = { 444, 462, 644, 662 };
            for (int i = 0; i < sidebands.Length; i++) inharm += Goertzel(motor, sidebands[i]);

            float harm = 0f;
            for (int i = 0; i < hc.Length; i++) harm += Goertzel(motor, hc[i]);

            float h2r = Goertzel(motor, hc[1]) / h1;
            float h3r = Goertzel(motor, hc[2]) / h1;
            float inhRatio = inharm / harm;

            sb.AppendLine();
            sb.AppendLine("    LEGACY sawtooth engine, for comparison (fundamental 62 cycles/loop):");
            float l1 = Goertzel(legacy, 62);
            int[] lb = { 31, 62, 124, 186, 248, 310 };
            for (int i = 0; i < lb.Length; i++)
                sb.AppendLine("      " + lb[i].ToString().PadLeft(5) + "  amp " + Goertzel(legacy, lb[i]).ToString("F5")
                              + "   /H1 " + (Goertzel(legacy, lb[i]) / l1).ToString("F4"));

            sb.AppendLine();
            sb.AppendLine("    " + Pass(h2r <= 0.35f) + "  H2/H1 = " + h2r.ToString("F4")
                          + "  (<= 0.35; sawtooth 0.500, legacy "
                          + (Goertzel(legacy, 124) / l1).ToString("F4") + ")");
            sb.AppendLine("    " + Pass(h3r <= 0.20f) + "  H3/H1 = " + h3r.ToString("F4")
                          + "  (<= 0.20; sawtooth 0.333, legacy "
                          + (Goertzel(legacy, 186) / l1).ToString("F4") + ")");
            sb.AppendLine("    " + Pass(inhRatio >= 0.10f) + "  inharmonic/harmonic amplitude = "
                          + inhRatio.ToString("F4") + "  (>= 0.10 - an exhaust note has none)");
            sb.AppendLine();
        }

        // ------------------------------------------------------------- [2] smoothness

        private static void Smoothness(StringBuilder sb, float[] motor, float[] legacy)
        {
            float mStep = MaxStep(motor), lStep = MaxStep(legacy);
            float mPeak = Peak(motor), mRms = Rms(motor);
            float lPeak = Peak(legacy), lRms = Rms(legacy);

            sb.AppendLine("[2] WAVEFORM - is there an exhaust edge in it?");
            sb.AppendLine("    largest single-sample jump   motor " + mStep.ToString("F5")
                          + "   legacy " + lStep.ToString("F5")
                          + "   (x" + (lStep / mStep).ToString("F1") + ")");
            sb.AppendLine("    crest factor (peak/RMS)      motor " + (mPeak / mRms).ToString("F3")
                          + "     legacy " + (lPeak / lRms).ToString("F3"));
            sb.AppendLine("    " + Pass(mStep <= 0.05f) + "  motor max step " + mStep.ToString("F5")
                          + " <= 0.050 - no reset, no buzz");
            sb.AppendLine("    " + Pass(mPeak / mRms <= 2.40f) + "  motor crest "
                          + (mPeak / mRms).ToString("F3") + " <= 2.40");
            sb.AppendLine("    " + Pass(mStep < lStep * 0.25f) + "  motor is smoother than the old engine by a wide margin");
            sb.AppendLine();
        }

        // ------------------------------------------------------------------ [3] seams

        private static void Seams(StringBuilder sb, float[] motor, float[] rush,
                                  float[] slip, float[] gravel)
        {
            sb.AppendLine("[3] LOOP SEAMS - the step from the last sample to the first, measured");
            sb.AppendLine("    against the largest ordinary step inside the same clip. A seam that");
            sb.AppendLine("    costs no more than an ordinary sample boundary cannot be heard as an");
            sb.AppendLine("    event. THRESHOLD: seam <= 1.00 x the largest interior step.");
            sb.AppendLine();
            bool all = true;
            all &= Seam(sb, "motor  (integer cycles)", motor);
            all &= Seam(sb, "rush   (overlap fold)", rush);
            all &= Seam(sb, "slip   (overlap fold)", slip);
            sb.AppendLine();
            sb.AppendLine("    The overlap fold in AudioDirector.Noise is a FIX made in this pass, and");
            sb.AppendLine("    the old cross-fade is rendered below so the claim is a measurement and");
            sb.AppendLine("    not an assertion. It left data[0] == data[n - fade], so at the seam the");
            sb.AppendLine("    loop jumped from sample n-1 to a sample 1102 places earlier - two");
            sb.AppendLine("    uncorrelated noise values, i.e. a click, twice a second for as long as");
            sb.AppendLine("    the car is on the grass:");
            Seam(sb, "slip,   OLD cross-fade", OldCrossFadedNoise(0.5f, 0.35f, 2600f, 1337));
            sb.AppendLine();
            sb.AppendLine("    (the grass loop is an imported file; its seam is checked in [8])");
            sb.AppendLine("    " + Pass(all) + "  every synthesised loop seam is within an ordinary sample step");
            sb.AppendLine();
        }

        private static bool Seam(StringBuilder sb, string label, float[] d)
        {
            float seam = Mathf.Abs(d[0] - d[d.Length - 1]);
            float mx = MaxStep(d);
            bool ok = seam <= mx;
            sb.AppendLine("    " + Pass(ok) + "  " + label.PadRight(24)
                          + " seam " + seam.ToString("F6")
                          + "   max interior step " + mx.ToString("F6")
                          + "   ratio " + (seam / mx).ToString("F3"));
            return ok;
        }

        // ----------------------------------------------------------- [4] pitch vs speed

        private static void PitchTracking(StringBuilder sb, float idle, float top)
        {
            sb.AppendLine("[4] PITCH vs SPEED - AudioDirector.MotorPitch, the shipping function.");
            bool mono = true;
            float prev = -1f;
            for (int i = 0; i <= 200; i++)
            {
                float s = i / 200f;
                float p = AudioDirector.MotorPitch(s, 0f, idle, top);
                if (p <= prev) mono = false;
                prev = p;
            }
            sb.AppendLine("    " + Pass(mono) + "  strictly increasing over 201 speeds at fixed load");
            sb.AppendLine("    speed  pitch   fundamental   whine partial (453 cyc)");
            float[] probe = { 0f, 0.25f, 0.50f, 0.75f, 1f };
            for (int i = 0; i < probe.Length; i++)
            {
                float p = AudioDirector.MotorPitch(probe[i], 0f, idle, top);
                sb.AppendLine("    " + probe[i].ToString("F2") + "   " + p.ToString("F3")
                              + "    " + (70f * p).ToString("F1").PadLeft(6) + " Hz"
                              + "     " + (453f * p).ToString("F0").PadLeft(6) + " Hz");
            }
            float hi = AudioDirector.MotorPitch(1f, 0f, idle, top);
            bool notShrill = 653f * hi <= 2000f;
            sb.AppendLine("    " + Pass(notShrill) + "  highest partial at top speed = "
                          + (653f * hi).ToString("F0") + " Hz (<= 2000 - never reaches the piercing band)");

            // Load moves the pitch a little and the LEVEL a lot; both directions checked.
            float onPower = AudioDirector.MotorPitch(0.8f, 1f, idle, top);
            float lifted = AudioDirector.MotorPitch(0.8f, -1f, idle, top);
            sb.AppendLine("    load term at speed 0.80: on power " + onPower.ToString("F4")
                          + ", lifted " + lifted.ToString("F4")
                          + "   (" + (100f * (onPower / lifted - 1f)).ToString("F2") + "% apart)");
            sb.AppendLine("    " + Pass(onPower > lifted) + "  power raises the pitch, lifting lowers it");
            sb.AppendLine();
        }

        // -------------------------------------------------------------- [5] stepping

        private static void PitchStepping(StringBuilder sb, float idle, float top,
                                          float pitchSmooth, float pitchRate,
                                          float gainSmooth, float gainRate, float loadSmooth)
        {
            sb.AppendLine("[5] STEPPING - the real smoothing driven over real speed profiles at 60 FPS.");
            sb.AppendLine("    The hard slew cap makes the bound a GUARANTEE: no frame may move the");
            sb.AppendLine("    pitch by more than motorPitchMaxRate x dt = "
                          + (pitchRate * Step).ToString("F4") + ".");
            sb.AppendLine();
            sb.AppendLine("    profile                       frames  pitch start -> end   max dPitch/frame  max dGain/frame");

            bool all = true;
            all &= Profile(sb, "launch 0 -> 1 over 4.0 s", Ramp(0f, 1f, 4f), true,
                           idle, top, pitchSmooth, pitchRate, gainSmooth, gainRate, loadSmooth, 0.010f);
            all &= Profile(sb, "lift 0.85 -> 0.55 / 1.5 s", Ramp(0.85f, 0.55f, 1.5f), false,
                           idle, top, pitchSmooth, pitchRate, gainSmooth, gainRate, loadSmooth, 0.010f);
            all &= Profile(sb, "braking 0.95 -> 0.35 / 1.2 s", Ramp(0.95f, 0.35f, 1.2f), false,
                           idle, top, pitchSmooth, pitchRate, gainSmooth, gainRate, loadSmooth, 0.020f);
            all &= Profile(sb, "IMPACT 0.90 -> 0.15 in 1 frame", Impact(0.90f, 0.15f), false,
                           idle, top, pitchSmooth, pitchRate, gainSmooth, gainRate, loadSmooth,
                           pitchRate * Step + 1e-4f);

            sb.AppendLine();
            sb.AppendLine("    " + Pass(all) + "  no profile steps the pitch audibly, including the impact");
            sb.AppendLine("    A barrier impact removes 83% of the car's speed in one simulation step.");
            sb.AppendLine("    The exponential ease alone would move the pitch by 8% of that gap in");
            sb.AppendLine("    one frame - a clearly audible drop. The slew cap is what makes it a");
            sb.AppendLine("    glide instead, and it is why the cap exists as well as the ease.");
            sb.AppendLine();
        }

        private static float[] Ramp(float from, float to, float seconds)
        {
            int n = Mathf.Max(2, (int)(seconds / Step)) + 90;   // 1.5 s of settling after
            float[] s = new float[n];
            int ramp = Mathf.Max(1, (int)(seconds / Step));
            for (int i = 0; i < n; i++)
                s[i] = i < ramp ? Mathf.Lerp(from, to, i / (float)ramp) : to;
            return s;
        }

        private static float[] Impact(float from, float to)
        {
            float[] s = new float[180];
            for (int i = 0; i < s.Length; i++) s[i] = i < 30 ? from : to;
            return s;
        }

        private static bool Profile(StringBuilder sb, string label, float[] speed, bool rising,
                                    float idle, float top, float pitchSmooth, float pitchRate,
                                    float gainSmooth, float gainRate, float loadSmooth,
                                    float allowedPitchStep)
        {
            // Start settled at the profile's opening speed: the game would have driven up
            // to it, so starting from idle would measure the probe's own catch-up ramp.
            float pitch = AudioDirector.MotorPitch(speed[0], 0f, idle, top);
            float gain = AudioDirector.MotorLevel(speed[0], 0f), load = 0f;
            float last = speed[0];
            float maxP = 0f, maxG = 0f, first = 0f;
            for (int i = 0; i < speed.Length; i++)
            {
                float raw = Mathf.Clamp((speed[i] - last) / Step / AudioDirector.LoadReferenceRate, -1f, 1f);
                last = speed[i];
                load = Mathf.Lerp(load, raw, 1f - Mathf.Exp(-loadSmooth * Step));

                float p = AudioDirector.Smooth(pitch, AudioDirector.MotorPitch(speed[i], load, idle, top),
                                               pitchSmooth, pitchRate, Step);
                float g = AudioDirector.Smooth(gain, AudioDirector.MotorLevel(speed[i], load),
                                               gainSmooth, gainRate, Step);
                if (i == 0) first = p;
                else
                {
                    maxP = Mathf.Max(maxP, Mathf.Abs(p - pitch));
                    maxG = Mathf.Max(maxG, Mathf.Abs(g - gain));
                }
                pitch = p; gain = g;
            }

            bool direction = rising ? pitch > first : pitch < first;
            bool bounded = maxP <= allowedPitchStep && maxP <= pitchRate * Step + 1e-4f;
            bool ok = direction && bounded;
            sb.AppendLine("    " + Pass(ok) + "  " + label.PadRight(30)
                          + speed.Length.ToString().PadLeft(4) + "   "
                          + first.ToString("F3") + " -> " + pitch.ToString("F3") + "        "
                          + maxP.ToString("F5") + "           " + maxG.ToString("F5"));
            return ok;
        }

        // ---------------------------------------------------------------- [6] levels

        private static void Levels(StringBuilder sb, float[] motor, float[] rush, float[] legacy,
                                   float motorVol, float rushVol, float master)
        {
            sb.AppendLine("[6] LEVEL vs THE OLD ENGINE. Both rendered, both resampled to the playback");
            sb.AppendLine("    rate the runtime would use at that speed, both scaled by the mix value");
            sb.AppendLine("    that actually applies (old: engineVolume 0.16 from Main.unity).");
            sb.AppendLine();
            sb.AppendLine("    clip RMS, unscaled:  motor " + Rms(motor).ToString("F4")
                          + "   rush " + Rms(rush).ToString("F4")
                          + "   legacy " + Rms(legacy).ToString("F4"));
            sb.AppendLine("    clip peak, unscaled: motor " + Peak(motor).ToString("F4")
                          + "   rush " + Peak(rush).ToString("F4")
                          + "   legacy " + Peak(legacy).ToString("F4"));
            sb.AppendLine("    (the clips themselves are the same loudness - ALL of the reduction is");
            sb.AppendLine("     in the mix value and the level shaping, so it is visible and tunable)");
            sb.AppendLine();
            sb.AppendLine("    speed  load    NEW rms     NEW A-wt    OLD rms     OLD A-wt    dB flat   dB A-wt");

            float[] speeds = { 0.15f, 0.45f, 0.62f, 0.80f, 1.00f };
            float[] loads = { 0.80f, 0.60f, 0.30f, 0.20f, 0.00f };
            bool all = true;
            float worst = 99f;
            for (int i = 0; i < speeds.Length; i++)
            {
                float s = speeds[i], l = loads[i];
                float[] nm = New(motor, rush, s, l, motorVol, rushVol, master);
                float[] ol = Old(legacy, s, master);
                float nr = Rms(nm), na = Rms(AWeight(nm));
                float or_ = Rms(ol), oa = Rms(AWeight(ol));
                float dFlat = Db(nr / or_), dA = Db(na / oa);
                worst = Mathf.Min(worst, -dA);
                all &= dA <= -6f;
                sb.AppendLine("    " + s.ToString("F2") + "   " + l.ToString("F2") + "   "
                              + nr.ToString("F5").PadLeft(9) + "   " + na.ToString("F5").PadLeft(9) + "   "
                              + or_.ToString("F5").PadLeft(9) + "   " + oa.ToString("F5").PadLeft(9) + "   "
                              + dFlat.ToString("F1").PadLeft(6) + "    " + dA.ToString("F1").PadLeft(6));
            }

            // A lift has to be audible, and it is a level change, not a pitch change.
            // Measured on the MOTOR alone, because that is what the load term controls:
            // the rush is tyre and wind noise and it is right that lifting does not change
            // it. The combined figure is reported too, since that is what is heard.
            float[] mOn = MotorOnly(motor, 0.80f, 0.20f, motorVol, master);
            float[] mOff = MotorOnly(motor, 0.80f, -0.80f, motorVol, master);
            float liftDb = Db(Rms(AWeight(mOff)) / Rms(AWeight(mOn)));
            float[] onPower = New(motor, rush, 0.80f, 0.20f, motorVol, rushVol, master);
            float[] lifted = New(motor, rush, 0.80f, -0.80f, motorVol, rushVol, master);
            float liftMixDb = Db(Rms(AWeight(lifted)) / Rms(AWeight(onPower)));
            sb.AppendLine();
            sb.AppendLine("    " + Pass(all) + "  the motor is at least 6 dB(A) below the old engine at every speed"
                          + "   (worst case " + worst.ToString("F1") + " dB quieter)");
            sb.AppendLine("    " + Pass(liftDb <= -3.5f) + "  lifting at 0.80 drops the MOTOR by "
                          + (-liftDb).ToString("F1") + " dB(A)  (>= 3.5 - a lift must be audible)");
            sb.AppendLine("           and the motor+rush mix by " + (-liftMixDb).ToString("F1")
                          + " dB(A), which is what the player actually hears");

            // And the top end must not be the loudest part of the lap.
            float peakSpeed = 0f, peakLevel = 0f;
            for (int i = 0; i <= 100; i++)
            {
                float lv = AudioDirector.MotorLevel(i / 100f, 0f);
                if (lv > peakLevel) { peakLevel = lv; peakSpeed = i / 100f; }
            }
            bool tapered = peakSpeed < 0.95f
                        && AudioDirector.MotorLevel(1f, 0f) < AudioDirector.MotorLevel(peakSpeed, 0f) * 0.92f;
            sb.AppendLine("    " + Pass(tapered) + "  the motor's loudest point is speed "
                          + peakSpeed.ToString("F2") + ", not the limiter"
                          + "   (top speed is " + Db(AudioDirector.MotorLevel(1f, 0f) / peakLevel).ToString("F1")
                          + " dB below it) - it does not scream flat out");
            sb.AppendLine();
        }

        // ----------------------------------------------------------- [7] readability

        private static void Readability(StringBuilder sb, float[] motor, float[] rush,
                                        float[] slip, float[] gravel, float[] beep,
                                        float[] impact, float[] finish,
                                        float motorVol, float rushVol, float master, float grassVolume)
        {
            sb.AppendLine("[7] READABILITY - every cue that has to stay above the motor, measured");
            sb.AppendLine("    A-weighted against it at four speeds. Scene mix values are used for");
            sb.AppendLine("    the cues (master " + SceneMaster.ToString("F2")
                          + ", slip " + SceneSlip.ToString("F2")
                          + ", impact " + SceneImpact.ToString("F2")
                          + ", ui " + SceneUi.ToString("F2") + ").");
            sb.AppendLine();

            // The cues, at the level and playback rate the runtime gives them.
            float slipMid = Rms(AWeight(Resample(slip, 1.15f))) * 0.6f * SceneSlip * SceneMaster;
            float slipMax = Rms(AWeight(Resample(slip, 1.35f))) * 1.0f * SceneSlip * SceneMaster;
            float grassRms = Rms(AWeight(gravel)) * grassVolume * SceneMaster;
            float beepRms = Rms(AWeight(beep)) * SceneUi * SceneMaster;
            float finishRms = Rms(AWeight(finish)) * SceneUi * SceneMaster;
            float impactRms = Rms(AWeight(impact)) * SceneImpact * SceneMaster;

            sb.AppendLine("    cue                             A-wt RMS");
            sb.AppendLine("    tyre slip, half lock             " + slipMid.ToString("F5"));
            sb.AppendLine("    tyre slip, full scrub            " + slipMax.ToString("F5"));
            sb.AppendLine("    grass overlay (full)             " + grassRms.ToString("F5"));
            sb.AppendLine("    countdown beep                   " + beepRms.ToString("F5"));
            sb.AppendLine("    finish stinger                   " + finishRms.ToString("F5"));
            sb.AppendLine("    barrier impact (full strength)   " + impactRms.ToString("F5"));
            sb.AppendLine();
            sb.AppendLine("    speed  motor A-wt   vs slip/2   vs slip    vs grass   vs beep   vs finish  vs impact");

            float[] speeds = { 0.15f, 0.45f, 0.80f, 1.00f };
            float[] loads = { 0.80f, 0.60f, 0.20f, 0.00f };
            bool all = true;
            for (int i = 0; i < speeds.Length; i++)
            {
                float m = Rms(AWeight(New(motor, rush, speeds[i], loads[i], motorVol, rushVol, master)));
                float a = Db(m / slipMid), b = Db(m / slipMax), c = Db(m / grassRms);
                float e = Db(m / beepRms), f = Db(m / finishRms), g = Db(m / impactRms);
                bool ok = a < 0f && b < 0f && e < 0f && f < 0f && g < 0f;
                all &= ok;
                sb.AppendLine("    " + (ok ? " " : "!") + speeds[i].ToString("F2") + "   "
                              + m.ToString("F5").PadLeft(9) + "   "
                              + a.ToString("F1").PadLeft(7) + "   " + b.ToString("F1").PadLeft(7) + "   "
                              + c.ToString("F1").PadLeft(7) + "   " + e.ToString("F1").PadLeft(7) + "   "
                              + f.ToString("F1").PadLeft(8) + "   " + g.ToString("F1").PadLeft(8));
            }
            sb.AppendLine("    (negative = the motor is BELOW that cue, which is the whole point; the grass column is informational - grass is an overlay that must sit under the motor, asserted in [8])");
            sb.AppendLine();
            sb.AppendLine("    " + Pass(all) + "  the motor sits under every cue at every speed");
            sb.AppendLine();
            sb.AppendLine("PAYLOAD: the grass loop (~23 KB Ogg) and the pickup zap; the rest is synthesised at boot; the whole audio");
            sb.AppendLine("system adds almost nothing to the download, which matters given that the art has");
            sb.AppendLine("taken the size budget to the ceiling (BUILD_BASELINE Build 6).");
            sb.AppendLine("RUNTIME: AudioSources all created in Awake and reused; synthesised clips totalling "
                          + ((motor.Length + rush.Length + slip.Length + beep.Length
                              + impact.Length + finish.Length + AudioDirector.BuildGoSamples().Length)
                             * 2 / 1024) + " KB of 16-bit PCM in memory.");
        }

        // ----------------------------------------------------- the old engine, verbatim

        /// <summary>
        /// The pre-EV <c>AudioDirector.Engine()</c>, copied here UNCHANGED and kept only
        /// as the measurement baseline. It lives in the probe rather than in the shipping
        /// class so that the comparison costs the build nothing; the numbers in case [6]
        /// are meaningless without it, and "it is quieter" is not a claim worth making
        /// without the thing it is quieter than.
        /// </summary>
        private static float[] LegacySawtoothEngine()
        {
            const float hz = 62f, seconds = 1f;
            int n = (int)(SR * seconds);
            float[] data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                float ph = t * hz * seconds;
                float v = Saw(ph) * 0.55f
                        + Saw(ph * 2f) * 0.22f
                        + Saw(ph * 0.5f) * 0.28f
                        + Mathf.Sin(ph * 4f * Mathf.PI * 2f) * 0.06f;
                v *= 0.85f + 0.15f * Mathf.Sin(t * Mathf.PI * 2f * 3f);
                data[i] = Mathf.Clamp(v * 0.5f, -1f, 1f);
            }
            return data;
        }

        private static float Saw(float phase)
        {
            float f = phase - Mathf.Floor(phase);
            return f * 2f - 1f;
        }

        /// <summary>
        /// The previous <c>AudioDirector.Noise</c> cross-fade, copied verbatim so case [3]
        /// can print the defect it fixed rather than describe it. The only difference from
        /// the shipping version is the last four lines.
        /// </summary>
        private static float[] OldCrossFadedNoise(float seconds, float amplitude, float cutoffHz, int seed)
        {
            int n = (int)(SR * seconds);
            float[] data = new float[n];
            UnityEngine.Random.State state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            float k = Mathf.Clamp01(cutoffHz / (SR * 0.5f));
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                lp += (UnityEngine.Random.value * 2f - 1f - lp) * k;
                data[i] = lp * amplitude;
            }
            UnityEngine.Random.state = state;

            int fade = Mathf.Min(n / 8, SR / 20);
            for (int i = 0; i < fade; i++)
            {
                float a = (float)i / fade;
                data[i] = Mathf.Lerp(data[n - fade + i], data[i], a);
            }
            return data;
        }


        // ------------------------------------------------------- [8] grass character

        private struct Character
        {
            public float crest, envCv, flatness, hfFraction, centroid, maxAutoCorr, seconds, rmsFlat, rmsA, peak;
        }

        private static Character Measure(float[] d)
        {
            Character c = new Character();
            c.seconds = d.Length / (float)SR;
            c.rmsFlat = Rms(d);
            c.rmsA = Rms(AWeight(d));
            float pk = 0f;
            for (int i = 0; i < d.Length; i++) pk = Mathf.Max(pk, Mathf.Abs(d[i]));
            c.peak = pk;
            c.crest = pk / Mathf.Max(1e-9f, c.rmsFlat);

            // envelope: RMS per 20 ms block, coefficient of variation
            int blk = SR / 50, nb = d.Length / blk;
            double sum = 0, sum2 = 0;
            for (int b = 0; b < nb; b++)
            {
                double e = 0;
                for (int i = 0; i < blk; i++) e += d[b * blk + i] * d[b * blk + i];
                double r = Math.Sqrt(e / blk);
                sum += r; sum2 += r * r;
            }
            double mean = sum / nb, var = Math.Max(0, sum2 / nb - mean * mean);
            c.envCv = (float)(Math.Sqrt(var) / Math.Max(1e-12, mean));

            // Welch power spectrum, 1024-point, Hann
            const int N = 1024;
            double[] pw = new double[N / 2];
            double[] re = new double[N], im = new double[N];
            int windows = 0;
            for (int s0 = 0; s0 + N <= d.Length; s0 += N / 2)
            {
                for (int i = 0; i < N; i++)
                {
                    re[i] = d[s0 + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)));
                    im[i] = 0;
                }
                Fft(re, im);
                for (int k = 0; k < N / 2; k++) pw[k] += re[k] * re[k] + im[k] * im[k];
                windows++;
            }
            double binHz = (double)SR / N, tot = 0, hf = 0, cen = 0;
            double logSum = 0, linSum = 0; int cnt = 0;
            for (int k = 1; k < N / 2; k++)
            {
                double f = k * binHz;
                tot += pw[k]; cen += pw[k] * f;
                if (f >= 1500) hf += pw[k];
                if (f >= 100 && f <= 5000) { logSum += Math.Log(pw[k] + 1e-18); linSum += pw[k]; cnt++; }
            }
            c.hfFraction = (float)(hf / Math.Max(1e-18, tot));
            c.centroid = (float)(cen / Math.Max(1e-18, tot));
            c.flatness = (float)(Math.Exp(logSum / cnt) / Math.Max(1e-18, linSum / cnt));

            // repeat: peak normalised autocorrelation at lags 0.3 s .. len-0.3 s (decimated by 4)
            int dec = 4, m = d.Length / dec;
            float[] x = new float[m];
            for (int i = 0; i < m; i++) x[i] = d[i * dec];
            double e0 = 0; for (int i = 0; i < m; i++) e0 += x[i] * x[i];
            int minLag = (int)(0.3f * SR / dec), maxLag = m - minLag;
            double best = 0;
            for (int lag = minLag; lag <= maxLag; lag += 6)
            {
                double a = 0;
                for (int i = 0; i < m; i++) a += x[i] * x[(i + lag) % m];
                best = Math.Max(best, a / Math.Max(1e-12, e0));
            }
            c.maxAutoCorr = (float)best;
            return c;
        }

        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                    }
                }
            }
        }

        private static float[] LoadGrass()
        {
            AudioClip c = Resources.Load<AudioClip>(AudioDirector.GrassClipPath);
            if (c == null) return new float[SR];
            c.LoadAudioData();
            float[] d = new float[c.samples * c.channels];
            c.GetData(d, 0);
            // The editor may decode at 44.1 kHz whatever the platform override says. The
            // content is band-limited to 3 kHz, so averaging pairs down to SR is lossless
            // enough for every measurement below.
            if (c.frequency == SR * 2)
            {
                float[] h = new float[d.Length / 2];
                for (int i = 0; i < h.Length; i++) h[i] = 0.5f * (d[2 * i] + d[2 * i + 1]);
                d = h;
            }
            return d;
        }

        private static void GrassClipChecks(StringBuilder sb, float[] g, float[] motor, float[] rush,
                                            float motorVol, float rushVol, float master, float grassVol)
        {
            sb.AppendLine();
            sb.AppendLine("[8] GRASS OVERLAY - the imported loop (Resources/" + AudioDirector.GrassClipPath
                          + "), trimmed, low-passed, cross-faded offline.");
            Character c = Measure(g);
            AudioClip clip = Resources.Load<AudioClip>(AudioDirector.GrassClipPath);
            sb.AppendLine("    clip: " + (clip != null ? clip.frequency + " Hz, " + clip.channels + " ch, " + clip.samples + " samples" : "missing") + "; length " + c.seconds.ToString("F2") + " s, peak " + c.peak.ToString("F3")
                          + ", RMS " + c.rmsFlat.ToString("F4") + ", spectral centroid " + c.centroid.ToString("F0")
                          + " Hz, energy above 1.5 kHz " + c.hfFraction.ToString("P1"));
            bool loaded = clip != null && g.Length > SR;
            sb.AppendLine("    " + Pass(loaded) + "  clip loads from Resources and is longer than 1 s");
            sb.AppendLine("    " + Pass(clip != null && clip.channels == 1) + "  clip is mono");
            Seam(sb, "grass loop seam", g);
            int ten = SR / 100;
            sb.AppendLine("    " + Pass(Rms(Slice(g, 0, ten)) > 0.2f * c.rmsFlat && Rms(Slice(g, g.Length - ten, ten)) > 0.2f * c.rmsFlat)
                          + "  no padding silence in the first / last 10 ms");
            sb.AppendLine("    " + Pass(c.peak <= 0.95f) + "  no clipping (peak " + c.peak.ToString("F3") + ")");

            float above4k = BandFraction(g, 4000f);
            sb.AppendLine("    " + Pass(above4k <= 0.08f) + "  energy above 4 kHz " + above4k.ToString("P1") + " <= 8% (no hiss)");

            // Level: under the motor at the speeds where grass is audible, and well under the old grass.
            float gr = Rms(AWeight(g)) * grassVol * master;
            bool under = true;
            float[] speeds = { 0.45f, 0.80f, 1.00f };
            float[] loads = { 0.60f, 0.20f, 0.00f };
            for (int i = 0; i < speeds.Length; i++)
            {
                float m = Rms(AWeight(New(motor, rush, speeds[i], loads[i], motorVol, rushVol, master)));
                sb.AppendLine("    speed " + speeds[i].ToString("F2") + ": motor A-wt " + m.ToString("F5")
                              + "  grass A-wt " + gr.ToString("F5") + "  (grass " + Db(gr / m).ToString("F1") + " dB re motor)");
                under &= Db(gr / m) <= 1f;
            }
            sb.AppendLine("    previous grain loop at the same mix measured 0.00574 A-wt RMS (full speed)");
            sb.AppendLine("    " + Pass(gr <= 0.00574f * 0.5f) + "  grass at least 6 dB(A) quieter than the old grass sound  ("
                          + Db(gr / 0.00574f).ToString("F1") + " dB)");
            sb.AppendLine("    " + Pass(under) + "  grass never more than 1 dB(A) above the motor (overlay, not dominant)");

            // Fade behaviour, through the shipping GrassFade at 60 FPS.
            float gain = 0f; int upFrames = 0;
            while (gain < 1f && upFrames < 1000) { gain = AudioDirector.GrassFade(gain, 1f, Step, 0.25f, 0.40f); upFrames++; }
            int downFrames = 0;
            while (gain > 0f && downFrames < 1000) { gain = AudioDirector.GrassFade(gain, 0f, Step, 0.25f, 0.40f); downFrames++; }
            sb.AppendLine("    " + Pass(Mathf.Abs(upFrames * Step - 0.25f) <= Step + 1e-4f)
                          + "  fade-in 0->1 takes " + (upFrames * Step).ToString("F3") + " s (0.25 s)");
            sb.AppendLine("    " + Pass(Mathf.Abs(downFrames * Step - 0.40f) <= Step + 1e-4f)
                          + "  fade-out 1->0 takes " + (downFrames * Step).ToString("F3") + " s (0.40 s)");

            // Bounce on/off the grass edge: the gain only ever ramps (bounded step, never a
            // jump); leaving for 0.1 s and coming back resumes from where the fade had got
            // to, not from zero - and the AudioSource is never stopped or restarted, only
            // its volume moves.
            gain = 1f; float maxStep = 0f;
            for (int i = 0; i < 6; i++) { float n = AudioDirector.GrassFade(gain, 0f, Step, 0.25f, 0.40f); maxStep = Mathf.Max(maxStep, Mathf.Abs(n - gain)); gain = n; }
            float atReentry = gain;
            float afterReentry = AudioDirector.GrassFade(gain, 1f, Step, 0.25f, 0.40f);
            maxStep = Mathf.Max(maxStep, Mathf.Abs(afterReentry - gain));
            for (int i = 0; i < 180; i++)
            {
                float target = ((i / 6) % 2 == 0) ? 1f : 0f;
                float n = AudioDirector.GrassFade(gain, target, Step, 0.25f, 0.40f);
                maxStep = Mathf.Max(maxStep, Mathf.Abs(n - gain));
                gain = n;
            }
            sb.AppendLine("    " + Pass(maxStep <= Step / 0.25f + 1e-4f)
                          + "  bouncing on/off the edge: max per-frame gain step " + maxStep.ToString("F3")
                          + " <= " + (Step / 0.25f).ToString("F3") + " (no jumps, no clicks)");
            sb.AppendLine("    " + Pass(atReentry > 0.5f && afterReentry > atReentry)
                          + "  0.1 s off the grass then back: gain " + atReentry.ToString("F2") + " -> "
                          + afterReentry.ToString("F2") + " (resumes the ramp, no restart from silence)");
            sb.AppendLine("    " + Pass(AudioDirector.GrassSpeedGain(0f) == 0f && AudioDirector.GrassSpeedGain(0.5f) == 1f)
                          + "  silent when stopped, full level by 40% speed");
            sb.AppendLine("    " + Pass(AudioDirector.GrassPitch(1f) > AudioDirector.GrassPitch(0.2f)
                          && AudioDirector.GrassPitch(1f) - AudioDirector.GrassPitch(0f) <= 0.10f)
                          + "  pitch rises slightly (<= 10%) with speed");
            sb.AppendLine();
        }

        private static float[] Slice(float[] d, int start, int len)
        {
            float[] o = new float[len];
            System.Array.Copy(d, start, o, 0, len);
            return o;
        }

        private static float BandFraction(float[] d, float aboveHz)
        {
            const int N = 1024;
            double[] re = new double[N], im = new double[N], pw = new double[N / 2];
            for (int s0 = 0; s0 + N <= d.Length; s0 += N / 2)
            {
                for (int i = 0; i < N; i++) { re[i] = d[s0 + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1))); im[i] = 0; }
                Fft(re, im);
                for (int k = 0; k < N / 2; k++) pw[k] += re[k] * re[k] + im[k] * im[k];
            }
            double tot = 0, hi = 0;
            for (int k = 1; k < N / 2; k++) { tot += pw[k]; if (k * (double)SR / N >= aboveHz) hi += pw[k]; }
            return (float)(hi / Math.Max(1e-18, tot));
        }

        // ------------------------------------------------------- [9] lifecycle / state

        private static void Lifecycle(StringBuilder sb)
        {
            sb.AppendLine("[9] SOUND LIFECYCLE - the button's state machine and the director's mute,");
            sb.AppendLine("    driven through the shipping code with a simulated AudioContext.");
            sb.AppendLine();
            bool all = true;

            // Evaluate: (muted, ctx, gesturesSinceLoss, msSinceGesture) -> status
            all &= Case(sb, "running, not muted -> On", AudioUnlockBridge.Evaluate(false, AudioCtxState.Running, 0, 999999) == SoundStatus.On);
            all &= Case(sb, "muted beats running -> Muted", AudioUnlockBridge.Evaluate(true, AudioCtxState.Running, 3, 10) == SoundStatus.Muted);
            all &= Case(sb, "muted beats suspended -> Muted", AudioUnlockBridge.Evaluate(true, AudioCtxState.Suspended, 0, 999999) == SoundStatus.Muted);
            all &= Case(sb, "suspended, no gesture yet -> WaitingForTap", AudioUnlockBridge.Evaluate(false, AudioCtxState.Suspended, 0, 999999) == SoundStatus.WaitingForTap);
            all &= Case(sb, "suspended, gesture 200 ms ago -> still Waiting (grace)", AudioUnlockBridge.Evaluate(false, AudioCtxState.Suspended, 1, 200) == SoundStatus.WaitingForTap);
            all &= Case(sb, "suspended, gesture 3 s ago -> Blocked", AudioUnlockBridge.Evaluate(false, AudioCtxState.Suspended, 1, 3000) == SoundStatus.Blocked);
            all &= Case(sb, "interrupted (iOS call), no gesture -> WaitingForTap", AudioUnlockBridge.Evaluate(false, AudioCtxState.Interrupted, 0, 999999) == SoundStatus.WaitingForTap);
            all &= Case(sb, "interrupted, tapped, still not running -> Blocked", AudioUnlockBridge.Evaluate(false, AudioCtxState.Interrupted, 2, 2500) == SoundStatus.Blocked);
            all &= Case(sb, "stalled clock never reads On", AudioUnlockBridge.Evaluate(false, AudioCtxState.Stalled, 0, 999999) != SoundStatus.On);
            all &= Case(sb, "no context (unsupported) never reads On", AudioUnlockBridge.Evaluate(false, AudioCtxState.None, 0, 999999) != SoundStatus.On);
            all &= Case(sb, "recovers: Interrupted(blocked) -> Running -> On", AudioUnlockBridge.Evaluate(false, AudioCtxState.Running, 2, 100) == SoundStatus.On);

            // tap rules
            all &= Case(sb, "tap before the race starts always toggles mute", SoundToggleView.TapTogglesMute(SoundStatus.WaitingForTap, false));
            all &= Case(sb, "tap when Blocked after start retries (does not mute)", !SoundToggleView.TapTogglesMute(SoundStatus.Blocked, true));
            all &= Case(sb, "tap when WaitingForTap after start retries", !SoundToggleView.TapTogglesMute(SoundStatus.WaitingForTap, true));
            all &= Case(sb, "tap when On or Muted toggles", SoundToggleView.TapTogglesMute(SoundStatus.On, true) && SoundToggleView.TapTogglesMute(SoundStatus.Muted, true));

            // director mute: mute -> unmute -> persisted
            int savedPref = PlayerPrefs.GetInt(AudioDirector.MutePrefKey, 0);
            float savedVol = AudioListener.volume;
            AudioCtxState savedSim = AudioUnlockBridge.Simulated;
            GameObject holder = new GameObject("AudioProbeLifecycle");
            holder.SetActive(false);
            AudioDirector d = holder.AddComponent<AudioDirector>();
            try
            {
                PlayerPrefs.SetInt(AudioDirector.MutePrefKey, 0);
                all &= Case(sb, "default: sound ON (not muted)", !d.Muted);
                d.SetMuted(true);
                all &= Case(sb, "mute: Muted, listener volume 0, pref persisted", d.Muted && AudioListener.volume == 0f
                            && PlayerPrefs.GetInt(AudioDirector.MutePrefKey, 0) == 1 && d.Status == SoundStatus.Muted);
                d.SetMuted(false);
                all &= Case(sb, "unmute: listener volume 1, pref cleared, status On", !d.Muted && AudioListener.volume == 1f
                            && PlayerPrefs.GetInt(AudioDirector.MutePrefKey, 0) == 0 && d.Status == SoundStatus.On);
                d.ToggleMuted(); d.ToggleMuted();
                all &= Case(sb, "toggle twice returns to unmuted", !d.Muted);

                AudioUnlockBridge.Simulated = AudioCtxState.Interrupted;
                d.SetMuted(true); d.SetMuted(false);
                all &= Case(sb, "interrupted context + unmute -> not On (no false claim)", d.Status != SoundStatus.On);
                AudioUnlockBridge.Simulated = AudioCtxState.Running;
                d.SetMuted(true); d.SetMuted(false);
                all &= Case(sb, "context back to running -> On", d.Status == SoundStatus.On);

                d.Unlock(); d.Unlock();
                all &= Case(sb, "Unlock is idempotent and flags unlocked", d.Unlocked);
            }
            finally
            {
                AudioUnlockBridge.Simulated = savedSim;
                PlayerPrefs.SetInt(AudioDirector.MutePrefKey, savedPref);
                AudioListener.volume = savedVol;
                UnityEngine.Object.DestroyImmediate(holder);
            }

            sb.AppendLine();
            sb.AppendLine("    " + Pass(all) + "  lifecycle / state machine");
            sb.AppendLine();
        }

        private static bool Case(StringBuilder sb, string label, bool ok)
        {
            sb.AppendLine("    " + Pass(ok) + "  " + label);
            return ok;
        }

        // --------------------------------------------------------------------- maths

        // The pitch range under test, captured from the component defaults in Run().
        private static float _idle = 0.50f, _top = 2.20f;

        /// <summary>The motor and rush as the mixer sums them at a given speed and load.</summary>
        private static float[] New(float[] motor, float[] rush, float speed, float load,
                                   float motorVol, float rushVol, float master)
        {
            float pitch = AudioDirector.MotorPitch(speed, load, _idle, _top);
            float[] m = Resample(motor, pitch);
            float[] r = Resample(rush, 0.85f + 0.40f * speed);
            float mg = AudioDirector.MotorLevel(speed, load) * motorVol * master;
            float rg = AudioDirector.RushLevel(speed) * rushVol * master;
            float[] o = new float[Mathf.Min(m.Length, r.Length)];
            for (int i = 0; i < o.Length; i++) o[i] = m[i] * mg + r[i] * rg;
            return o;
        }

        /// <summary>The motor alone, without the rush - for the load/lift measurement.</summary>
        private static float[] MotorOnly(float[] motor, float speed, float load,
                                         float motorVol, float master)
        {
            float[] m = Resample(motor, AudioDirector.MotorPitch(speed, load, _idle, _top));
            float g = AudioDirector.MotorLevel(speed, load) * motorVol * master;
            for (int i = 0; i < m.Length; i++) m[i] *= g;
            return m;
        }

        /// <summary>The old engine at the same speed: pitch from 0.75..1.90, no level shaping.</summary>
        private static float[] Old(float[] legacy, float speed, float master)
        {
            float[] e = Resample(legacy, Mathf.Lerp(0.75f, 1.90f, speed));
            float g = 0.16f * master;                   // engineVolume as Main.unity holds it
            for (int i = 0; i < e.Length; i++) e[i] *= g;
            return e;
        }

        private static float[] Resample(float[] src, float rate)
        {
            int n = src.Length;
            int m = Mathf.Max(16, (int)(n / Mathf.Max(0.01f, rate)));
            float[] o = new float[m];
            for (int i = 0; i < m; i++)
            {
                float p = i * rate;
                int j = ((int)p) % n;
                float f = p - (int)p;
                o[i] = src[j] * (1f - f) + src[(j + 1) % n] * f;
            }
            return o;
        }

        private static float Peak(float[] d)
        {
            float m = 0f;
            for (int i = 0; i < d.Length; i++) { float a = d[i] < 0f ? -d[i] : d[i]; if (a > m) m = a; }
            return m;
        }

        private static float Rms(float[] d)
        {
            double s = 0;
            for (int i = 0; i < d.Length; i++) s += (double)d[i] * d[i];
            return (float)Math.Sqrt(s / Math.Max(1, d.Length));
        }

        private static float MaxStep(float[] d)
        {
            float m = 0f;
            for (int i = 1; i < d.Length; i++)
            {
                float a = d[i] - d[i - 1]; if (a < 0f) a = -a;
                if (a > m) m = a;
            }
            return m;
        }

        private static float Db(float ratio)
        {
            return ratio <= 1e-9f ? -120f : 20f * Mathf.Log10(ratio);
        }

        /// <summary>
        /// Exact-bin Goertzel. The clip is one second at <see cref="SR"/>, so bin k is
        /// k cycles per loop with no windowing and no leakage - which is only true because
        /// the synthesis uses integer cycle counts, and is the reason it does.
        /// </summary>
        private static float Goertzel(float[] d, int k)
        {
            int n = d.Length;
            double w = 2.0 * Math.PI * k / n;
            double c = 2.0 * Math.Cos(w);
            double s1 = 0, s2 = 0;
            for (int i = 0; i < n; i++)
            {
                double s0 = d[i] + c * s1 - s2;
                s2 = s1; s1 = s0;
            }
            double re = s1 - s2 * Math.Cos(w);
            double im = s2 * Math.Sin(w);
            return (float)(2.0 * Math.Sqrt(re * re + im * im) / n);
        }

        // --- A-weighting (IEC 61672), as a cascade of biquads in double precision -------

        private static float[] AWeight(float[] d)
        {
            double[] y = new double[d.Length];
            for (int i = 0; i < d.Length; i++) y[i] = d[i];

            double[][] stages = AStages();
            for (int s = 0; s < stages.Length; s++) Biquad(y, stages[s]);

            float g = AReferenceGain();
            float[] o = new float[d.Length];
            for (int i = 0; i < d.Length; i++) o[i] = (float)(y[i] * g);
            return o;
        }

        private static double[][] AStages()
        {
            // Two poles at 20.6 Hz and two zeros at DC -> a 2nd-order high-pass; single
            // poles at 107.7 Hz and 737.9 Hz -> two 1st-order high-passes; two poles at
            // 12194 Hz -> a 2nd-order low-pass, clamped under Nyquist because the clips
            // are 22.05 kHz and there is nothing above 11 kHz to weight.
            return new[]
            {
                Hp2(20.598997), Hp1(107.65265), Hp1(737.86223), Lp2(Math.Min(12194.217, SR * 0.45))
            };
        }

        private static double[] Hp2(double fc)
        {
            double w = Math.Tan(Math.PI * fc / SR), n = 1.0 / (1 + Math.Sqrt(2) * w + w * w);
            return new[] { n, -2 * n, n, 2 * (w * w - 1) * n, (1 - Math.Sqrt(2) * w + w * w) * n };
        }

        private static double[] Lp2(double fc)
        {
            double w = Math.Tan(Math.PI * fc / SR), n = 1.0 / (1 + Math.Sqrt(2) * w + w * w);
            return new[] { w * w * n, 2 * w * w * n, w * w * n, 2 * (w * w - 1) * n, (1 - Math.Sqrt(2) * w + w * w) * n };
        }

        private static double[] Hp1(double fc)
        {
            double w = Math.Tan(Math.PI * fc / SR), n = 1.0 / (1 + w);
            return new[] { n, -n, 0.0, (w - 1) * n, 0.0 };
        }

        private static void Biquad(double[] y, double[] c)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double x = y[i];
                double v = c[0] * x + c[1] * x1 + c[2] * x2 - c[3] * y1 - c[4] * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = v;
                y[i] = v;
            }
        }

        private static float _aGain;

        /// <summary>Scale so that a 1 kHz sine comes out at unity, per the standard.</summary>
        private static float AReferenceGain()
        {
            if (_aGain > 0f) return _aGain;
            double[] refSig = new double[SR];
            for (int i = 0; i < SR; i++) refSig[i] = Math.Sin(2.0 * Math.PI * 1000.0 * i / SR);
            double inRms = 0;
            for (int i = SR / 2; i < SR; i++) inRms += refSig[i] * refSig[i];
            inRms = Math.Sqrt(inRms / (SR - SR / 2));

            double[] y = (double[])refSig.Clone();
            double[][] stages = AStages();
            for (int s = 0; s < stages.Length; s++) Biquad(y, stages[s]);
            double outRms = 0;
            for (int i = SR / 2; i < SR; i++) outRms += y[i] * y[i];
            outRms = Math.Sqrt(outRms / (SR - SR / 2));

            _aGain = (float)(inRms / Math.Max(1e-12, outRms));
            return _aGain;
        }
    }
}
