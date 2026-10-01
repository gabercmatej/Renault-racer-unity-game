using UnityEngine;
using RenaultRacer.Core;
using RenaultRacer.Vehicle;

namespace RenaultRacer.Audio
{
    /// <summary>
    /// Gameplay audio (plan section 15). Motor, tyre slip, off-road, impact, countdown
    /// and finish - the cues that tell the player what the car is doing.
    ///
    /// **Every clip is synthesised at boot rather than imported.** Synthesis costs the
    /// download nothing, has no import step and no licensing question, and it makes the
    /// mapping from game state to sound readable in code - which is the part that has to
    /// be right. The whole audio system contributes **zero bytes** to the payload, which
    /// matters: the art already spends the size budget (see BUILD_BASELINE Build 6).
    ///
    /// ## The car is ELECTRIC (Phase 10d)
    ///
    /// The previous <c>Engine()</c> was a stack of sawtooths - a combustion note. That is
    /// wrong for the car, and it is also the reason the old mix was tiring: a sawtooth has
    /// a sharp per-cycle reset, so its crest factor is high and its harmonics fall as 1/n,
    /// which is exactly the spectrum an exhaust pulse train has and exactly the spectrum
    /// the ear reads as "engine". Held at one pitch for a lap it is an angle grinder.
    ///
    /// The replacement is an inverter/motor whine, built from three ideas:
    ///
    /// 1. **A smooth tonal core.** Sine partials whose amplitudes fall much faster than
    ///    1/n (0.50 / 0.13 / 0.045 / 0.022 against a sawtooth's 0.50 / 0.33 / 0.25 / 0.20).
    ///    No sharp edge anywhere in the waveform, so no buzz.
    /// 2. **Inharmonic whine partials** at 167, 453 and 653 cycles per loop against a
    ///    70-cycle fundamental - ratios of 2.39, 6.47 and 9.33. Nothing in a harmonic
    ///    series sits there, and that mismatch is what makes a motor sound like a motor
    ///    rather than like a note. The 453 partial is amplitude-modulated (and the 653
    ///    counter-modulated) to stand in for PWM switching shimmer.
    /// 3. **A separate broadband rush** that grows with speed and is NOT locked to the
    ///    motor's pitch - tyre roar and wind, the part that carries speed once the tonal
    ///    whine has flattened off.
    ///
    /// ### Why the loop seam is inaudible
    ///
    /// Every term in the motor waveform - every partial, and every modulator applied to
    /// one - has an **integer number of cycles across the loop**. Sample <c>n</c> would
    /// therefore be bit-identical to sample 0, so the seam is not a cross-fade or a
    /// compromise: there is no discontinuity to hide. The rush clip is noise, which cannot
    /// do that, so it keeps the tail-into-head cross-fade <see cref="Noise"/> has always
    /// used. `AudioProbe` measures both.
    ///
    /// ### Why nothing steps audibly
    ///
    /// Pitch and gain each get TWO limits: an exponential ease (so they follow speed
    /// smoothly) and then a hard slew-rate clamp in units per second (so a collision that
    /// halves the car's speed in one step still cannot move the pitch more than
    /// <see cref="motorPitchMaxRate"/> x dt). The exponential alone is not enough - it
    /// moves a fixed FRACTION of the gap, so a big enough gap still steps. `dt` is capped
    /// too, so a frame hitch cannot smuggle a jump through the clamp.
    ///
    /// ### Load, not just speed
    ///
    /// Speed alone cannot tell you the driver lifted. <see cref="CarSimulation"/> does not
    /// publish throttle, so load is derived here from the smoothed rate of change of
    /// speed: full power is about +1, a lift about 0, braking about -1. It moves the motor
    /// gain by +/-45% (4.7 dB of motor level between full power and a hard lift) and the
    /// pitch by +/-1.5%. The gain is where a lift is
    /// actually heard; the pitch nudge is small on purpose, because an EV's frequency is
    /// tied to wheel speed and a large load-to-pitch term would read as a gearbox.
    ///
    /// ## Mobile-web rules, treated as hard constraints (plan section 15)
    ///
    /// - **Nothing plays before the first user gesture.** <see cref="Unlock"/> runs on the
    ///   tap that starts the race.
    /// - **Sources are pre-created and reused.** Five of them, made at boot, muted until
    ///   unlocked. No PlayClipAtPoint, no per-shot AudioSource, nothing allocates while
    ///   racing (plan 17.5).
    /// - **The game must be fully playable muted.** Every cue has a visual counterpart.
    /// - **Silent when backgrounded.**
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class AudioDirector : MonoBehaviour
    {
        [SerializeField] private CarController car;
        [SerializeField] private GameDirector director;

        [Header("Mix")]
        [Range(0f, 1f)] public float masterVolume = 0.45f;

        [Tooltip("The electric motor's tonal whine. Deliberately well under the cues: the " +
                 "motor is the one sound that never stops, so it is the one sound that " +
                 "must sit UNDERNEATH the game rather than on top of it. Renamed from " +
                 "engineVolume so the retune actually takes effect - the old name is still " +
                 "serialised in Main.unity at 0.16 and would have overridden this.")]
        [Range(0f, 1f)] public float motorVolume = 0.075f;

        [Tooltip("Broadband tyre/wind rush. Carries the top end so the whine does not have to.")]
        [Range(0f, 1f)] public float rushVolume = 0.045f;

        [Range(0f, 1f)] public float slipVolume = 0.22f;
        [Range(0f, 1f)] public float offRoadVolume = 0.18f;
        [Range(0f, 1f)] public float impactVolume = 0.4f;
        [Range(0f, 1f)] public float uiVolume = 0.4f;

        [Header("Motor")]
        [Tooltip("Playback rate of the motor loop at a standstill and at top speed. The " +
                 "70-cycle fundamental therefore sweeps 35 -> 154 Hz and the 453-cycle " +
                 "whine partial 227 -> 997 Hz: a believable EV pull that never reaches the " +
                 "piercing part of the spectrum.")]
        public float motorPitchIdle = 0.50f;
        public float motorPitchTop = 2.20f;

        [Tooltip("Exponential ease on the motor pitch, per second. Slower than the old " +
                 "engine's 8 because an EV's pitch is a long smooth sweep, not a rev.")]
        [Range(1f, 30f)] public float motorPitchSmoothing = 5f;

        [Tooltip("HARD ceiling on how fast the pitch may move, playback-rate units per " +
                 "second. This is what makes 'no audible stepping' a guarantee rather than " +
                 "a hope: at 60 FPS it caps a frame's change at 0.020.")]
        [Range(0.1f, 6f)] public float motorPitchMaxRate = 1.2f;

        [Tooltip("Exponential ease on the motor gain, per second.")]
        [Range(1f, 30f)] public float motorGainSmoothing = 6f;

        [Tooltip("HARD ceiling on the motor gain's rate of change, gain units per second.")]
        [Range(0.2f, 12f)] public float motorGainMaxRate = 2.5f;

        [Tooltip("Ease on the derived load term, per second. Raw frame-to-frame " +
                 "acceleration is far too noisy to drive a level with.")]
        [Range(1f, 30f)] public float loadSmoothing = 6f;

        /// <summary>
        /// Rate of change of <c>SpeedPct</c>, per second, that counts as full load. The
        /// shipping car covers 0 -> top in roughly four seconds, so 0.30 puts hard
        /// acceleration near +1 and hard braking at -1 without pinning either.
        /// </summary>
        public const float LoadReferenceRate = 0.30f;

        /// <summary>Longest frame the smoothing will honour. A hitch must not become a jump.</summary>
        public const float MaxSmoothingStep = 0.05f;

        private AudioSource _motor;
        private AudioSource _rush;
        private AudioSource _slip;
        private AudioSource _offRoad;
        private AudioSource _oneShot;

        private AudioClip _motorClip;
        private AudioClip _rushClip;
        private AudioClip _slipClip;
        private AudioClip _gravelClip;
        private AudioClip _beepClip;
        private AudioClip _goClip;
        private AudioClip _impactClip;
        private AudioClip _finishClip;

        private bool _unlocked;
        private float _pitch;
        private float _gain;
        private float _load;
        private float _lastSpeedPct;
        private bool _hasLastSpeed;
        private int _lastLamps = -1;
        private GameState _lastState = GameState.Boot;

        /// <summary>True once a user gesture has let the browser start audio.</summary>
        public bool Unlocked { get { return _unlocked; } }

        /// <summary>Smoothed motor playback rate, for the debug HUD and the probe.</summary>
        public float MotorPitchNow { get { return _pitch; } }

        /// <summary>Smoothed motor gain shape (before volume and master), 0..~1.1.</summary>
        public float MotorGainNow { get { return _gain; } }

        /// <summary>Derived load, -1 braking .. +1 full power.</summary>
        public float LoadNow { get { return _load; } }

        private void Awake()
        {
            if (car == null) car = FindAnyObjectByType<CarController>();
            if (director == null) director = FindAnyObjectByType<GameDirector>();

            BuildClips();

            // Every source exists from boot and is simply muted until unlocked. Creating
            // them on demand would allocate on the frame the player is least able to
            // tolerate a hitch - the one where they hit something.
            _motor = MakeSource("Motor", _motorClip, true, 0f);
            _rush = MakeSource("Rush", _rushClip, true, 0f);
            _slip = MakeSource("Slip", _slipClip, true, 0f);
            _offRoad = MakeSource("OffRoad", _gravelClip, true, 0f);
            _oneShot = MakeSource("OneShot", null, false, 1f);

            _pitch = motorPitchIdle;
            _gain = MotorLevel(0f, 0f);
        }

        private AudioSource MakeSource(string name, AudioClip clip, bool loop, float volume)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.loop = loop;
            s.volume = volume;
            s.playOnAwake = false;
            // 2D. There is no spatial audio in a pseudo-3D racer where the camera and
            // the car are the same point, and 3D panning would only cost cycles.
            s.spatialBlend = 0f;
            return s;
        }

        /// <summary>
        /// Start audio. MUST be called from a real user gesture - the tap that starts
        /// the race. Safe to call repeatedly.
        /// </summary>
        public void Unlock()
        {
            if (_unlocked) return;
            _unlocked = true;

            // A browser that still refuses will simply leave these not playing; the
            // game is designed to be fully understandable muted, so that is survivable
            // rather than fatal (plan R-M5).
            _motor.Play();
            _rush.Play();
            _slip.Play();
            _offRoad.Play();
        }

        private void Update()
        {
            if (!_unlocked || car == null || car.Sim == null) return;

            CarSimulation sim = car.Sim;
            bool racing = director != null && director.State == GameState.Racing;
            float master = masterVolume;
            float dt = Mathf.Min(Time.deltaTime, MaxSmoothingStep);
            float pct = Mathf.Clamp01(sim.SpeedPct);

            // --- load, derived: the simulation does not publish throttle ------------
            float rawLoad = 0f;
            if (_hasLastSpeed && dt > 0.0001f)
                rawLoad = Mathf.Clamp((pct - _lastSpeedPct) / dt / LoadReferenceRate, -1f, 1f);
            _lastSpeedPct = pct;
            _hasLastSpeed = true;
            _load = Mathf.Lerp(_load, rawLoad, 1f - Mathf.Exp(-loadSmoothing * dt));

            // --- motor: pitch and gain, both eased AND slew-limited ------------------
            _pitch = Smooth(_pitch, MotorPitch(pct, _load, motorPitchIdle, motorPitchTop),
                            motorPitchSmoothing, motorPitchMaxRate, dt);
            _gain = Smooth(_gain, MotorLevel(pct, _load),
                           motorGainSmoothing, motorGainMaxRate, dt);

            _motor.pitch = _pitch;
            _motor.volume = _gain * motorVolume * master * (racing ? 1f : 0.25f);

            // The rush is not locked to the motor: it is tyre and wind noise, so it only
            // brightens a little with speed. Locking it would turn it into part of the
            // whine and undo the point of having it.
            _rush.pitch = 0.85f + 0.40f * pct;
            _rush.volume = RushLevel(pct) * rushVolume * master * (racing ? 1f : 0.25f);

            // --- tyre slip: the drift cue -----------------------------------------
            // Tyres squeal when they SCRUB, so this reads SlipRatio. It used to read
            // road-relative lateral velocity, which since plan 8.16 means "crossing the
            // road" rather than "sliding" - a car placed diagonally but rolling true was
            // squealing for nothing, and a car sliding straight down the road was silent.
            float slip = Mathf.Max(sim.SlipRatio, sim.DriftAmount * sim.SlipRatio * 1.5f)
                       * Mathf.Clamp01(pct * 2f);
            _slip.volume = slip * slipVolume * master;
            _slip.pitch = 0.85f + 0.5f * pct;

            // --- off-road ----------------------------------------------------------
            float off = sim.OffRoad ? Mathf.Clamp01(pct * 2.5f) : 0f;
            _offRoad.volume = off * offRoadVolume * master;

            TickEvents();
        }

        // ---------------------------------------------------- the mapping, as pure code
        //
        // These are static and side-effect free so that AudioProbe drives THE SAME code
        // the game does. A probe that re-implements the mapping proves only that someone
        // typed it twice.

        /// <summary>
        /// Motor playback rate. Linear in speed, because an EV's electrical frequency is
        /// proportional to wheel speed - which also means the perceived (logarithmic)
        /// pitch change compresses toward the top, so the car does not scream at the
        /// limiter. Strictly increasing in <paramref name="speedPct"/>.
        /// </summary>
        public static float MotorPitch(float speedPct, float load, float idle, float top)
        {
            float s = Mathf.Clamp01(speedPct);
            return Mathf.Lerp(idle, top, s) * (1f + 0.015f * Mathf.Clamp(load, -1f, 1f));
        }

        /// <summary>Speed above which the motor's level starts coming back DOWN.</summary>
        public const float MotorTaperStart = 0.62f;

        /// <summary>
        /// Motor gain shape, before <see cref="motorVolume"/> and master.
        ///
        /// Three things at once: a square-root rise so the cue is strongest in the low
        /// and middle speeds where the player is actually judging it; a 30% TAPER over
        /// the top third, so the loudest point of the lap is around 62% of top speed and
        /// not the long flat-out straight; and the load term, which is what makes a lift
        /// audible.
        /// </summary>
        public static float MotorLevel(float speedPct, float load)
        {
            float s = Mathf.Clamp01(speedPct);
            float body = 0.10f + 0.90f * Mathf.Sqrt(s);
            float taper = 1f - 0.30f * Mathf.Clamp01((s - MotorTaperStart) / (1f - MotorTaperStart));
            float loadTerm = 1f + 0.45f * Mathf.Clamp(load, -1f, 1f);
            return body * taper * loadTerm;
        }

        /// <summary>Broadband rush gain shape. Quadratic: nothing at a crawl, real at speed.</summary>
        public static float RushLevel(float speedPct)
        {
            float s = Mathf.Clamp01(speedPct);
            return s * s;
        }

        /// <summary>
        /// Ease toward a target AND clamp the rate. Returns the new value.
        /// The guarantee is the second half: <c>|result - current| &lt;= maxRate * dt</c>,
        /// whatever the target does.
        /// </summary>
        public static float Smooth(float current, float target, float smoothing, float maxRate, float dt)
        {
            float eased = Mathf.Lerp(current, target, 1f - Mathf.Exp(-smoothing * dt));
            return Mathf.MoveTowards(current, eased, maxRate * dt);
        }

        // ------------------------------------------------------------------- events

        private void TickEvents()
        {
            if (director == null) return;

            // Countdown: one beep per lamp, a different note on the green.
            if (director.State == GameState.Countdown)
            {
                if (director.LampsLit != _lastLamps)
                {
                    _lastLamps = director.LampsLit;
                    if (_lastLamps > 0) PlayOneShot(_beepClip, uiVolume);
                }
            }
            else _lastLamps = -1;

            if (director.State != _lastState)
            {
                GameState previous = _lastState;
                _lastState = director.State;

                if (director.State == GameState.Racing && previous == GameState.Countdown)
                    PlayOneShot(_goClip, uiVolume);
                else if (director.State == GameState.Finished)
                    PlayOneShot(_finishClip, uiVolume);
            }

            // Impact is polled from the simulation rather than pushed, for the same
            // reason CarController polls it: an event would allocate a delegate call in
            // the hot path (see CarSimulation.ConsumeImpact).
            if (car != null && car.Sim != null)
            {
                float impact = _impactPeek;
                _impactPeek = 0f;
                if (impact > 0.02f) PlayOneShot(_impactClip, impactVolume * impact);
            }
        }

        private float _impactPeek;

        /// <summary>
        /// Told about an impact by CarController, which is the one place that consumes
        /// it. Audio cannot consume it itself without stealing it from the camera shake
        /// and the sparks.
        /// </summary>
        public void ReportImpact(float strength)
        {
            if (strength > _impactPeek) _impactPeek = strength;
        }

        private void PlayOneShot(AudioClip clip, float volume)
        {
            if (!_unlocked || clip == null) return;
            // PlayOneShot on a pre-created source, never PlayClipAtPoint: the latter
            // creates and destroys a GameObject per sound (plan section 15).
            _oneShot.PlayOneShot(clip, Mathf.Clamp01(volume * masterVolume));
        }

        /// <summary>
        /// A backgrounded tab must be silent - battery, and nobody wants motor noise
        /// from a page they have navigated away from (plan section 15).
        /// </summary>
        private void OnApplicationPause(bool paused) { AudioListener.pause = paused; }
        private void OnApplicationFocus(bool focused) { AudioListener.pause = !focused; }

        // ------------------------------------------------------------- synthesis

        public const int SampleRate = 22050;   // plenty for a motor and noise; half the data
        private const float Tau = Mathf.PI * 2f;

        // --- the motor waveform, as data so the probe can assert against the SAME table.
        //
        // Everything is expressed in CYCLES PER LOOP, not in Hz, and every number here is
        // an integer. That is the whole seam argument: an integer number of cycles across
        // the loop means the waveform arrives back exactly where it started.

        /// <summary>Loop length. One second is long enough that the ear cannot clock it.</summary>
        public const float MotorLoopSeconds = 1f;

        /// <summary>The harmonic stack. Note how fast it falls compared with a sawtooth.</summary>
        public static readonly int[] MotorHarmonicCycles = { 70, 140, 210, 280 };
        public static readonly float[] MotorHarmonicGains = { 0.50f, 0.13f, 0.045f, 0.022f };

        /// <summary>A sawtooth's stack, for comparison only - 1/n. AudioProbe uses it.</summary>
        public static readonly float[] SawtoothGainsForReference = { 0.50f, 0.25f, 0.1667f, 0.125f };

        /// <summary>
        /// The whine. 167/70 = 2.39, 453/70 = 6.47, 653/70 = 9.33 - deliberately nowhere
        /// near an integer, which is what stops the ear resolving this as a musical note.
        /// </summary>
        public static readonly int[] MotorWhineCycles = { 167, 453, 653 };
        public static readonly float[] MotorWhineGains = { 0.085f, 0.105f, 0.048f };

        /// <summary>PWM shimmer rate and the slow body breath, both integer cycles/loop.</summary>
        public const int MotorPwmCycles = 9;
        public const int MotorBodyCycles = 3;

        /// <summary>Peak the motor loop is normalised to. See AudioProbe for the levels.</summary>
        public const float MotorPeak = 0.40f;

        /// <summary>Peak the rush loop is normalised to.</summary>
        public const float RushPeak = 0.50f;

        private void BuildClips()
        {
            _motorClip = Make("MotorLoop", BuildMotorSamples());
            _rushClip = Make("MotorRush", BuildRushSamples());
            _slipClip = Make("TyreSlip", BuildSlipSamples());
            _gravelClip = Make("OffRoad", BuildGravelSamples());
            _beepClip = Make("Beep", BuildBeepSamples());
            _goClip = Make("Go", BuildGoSamples());
            _impactClip = Make("Impact", BuildImpactSamples());
            _finishClip = Make("Finish", BuildFinishSamples());
        }

        /// <summary>
        /// The electric motor loop. Read the class comment for why it sounds like one;
        /// what matters here is that there is no discontinuity anywhere in the waveform
        /// (every component is a sine) and none at the seam (every component completes a
        /// whole number of cycles).
        /// </summary>
        public static float[] BuildMotorSamples()
        {
            int n = (int)(SampleRate * MotorLoopSeconds);
            float[] d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;              // 0..1 across exactly one loop

                float v = 0f;
                for (int h = 0; h < MotorHarmonicCycles.Length; h++)
                    v += Mathf.Sin(Tau * MotorHarmonicCycles[h] * t) * MotorHarmonicGains[h];

                // The inverter shimmer. The switching partial breathes and its octave-ish
                // neighbour breathes against it, which is the thing that reads as
                // "electric" rather than as "a synth pad".
                float pwm = 0.72f + 0.28f * Mathf.Sin(Tau * MotorPwmCycles * t);
                v += Mathf.Sin(Tau * MotorWhineCycles[0] * t) * MotorWhineGains[0];
                v += Mathf.Sin(Tau * MotorWhineCycles[1] * t) * MotorWhineGains[1] * pwm;
                v += Mathf.Sin(Tau * MotorWhineCycles[2] * t) * MotorWhineGains[2] * (1.42f - 0.42f * pwm);

                // A very shallow breath on the body. The old engine used +/-15% here and
                // that tremolo is a large part of why a lap of it was tiring; this is
                // +/-3.5%, about a third of a decibel.
                v *= 0.965f + 0.035f * Mathf.Sin(Tau * MotorBodyCycles * t);

                d[i] = v;
            }
            Normalise(d, MotorPeak);
            return d;
        }

        /// <summary>
        /// Tyre and wind rush. Dark, not hissy - cutting it off at 1100 Hz keeps it under
        /// the whine instead of competing with it, and keeps it from sounding like static.
        /// </summary>
        public static float[] BuildRushSamples()
        {
            float[] d = Noise(1f, 1f, 1100f, 4242);
            Normalise(d, RushPeak);
            return d;
        }

        public static float[] BuildSlipSamples() { return Noise(0.5f, 0.35f, 2600f, 1337); }
        public static float[] BuildGravelSamples() { return Noise(0.5f, 0.9f, 900f, 1337); }
        public static float[] BuildBeepSamples() { return Tone(0.09f, 720f, 0.35f); }
        public static float[] BuildGoSamples() { return Tone(0.32f, 1180f, 0.45f); }

        /// <summary>
        /// Filtered noise - tyre squeal at a high cutoff, gravel at a low one. A
        /// one-pole low-pass is enough to tell the two apart.
        ///
        /// **The loop seam is an overlap-ADD fold, and this is a fix.** The previous
        /// version synthesised exactly <c>n</c> samples and then blended the head toward
        /// <c>data[n - fade + i]</c>, which left <c>data[0] == data[n - fade]</c>. At
        /// playback the seam therefore jumped from sample <c>n-1</c> to a sample 1102
        /// places earlier - two uncorrelated noise values, i.e. a click, twice a second
        /// for as long as the car was on the grass.
        ///
        /// The fix is to synthesise <c>n + fade</c> samples and fold the overhang back
        /// over the head, so <c>data[0]</c> is the sample that genuinely FOLLOWS
        /// <c>data[n - 1]</c>. The seam then costs one ordinary sample step.
        /// `AudioProbe` case [3] renders BOTH versions and prints the two seam steps, so
        /// this paragraph is a measurement rather than a claim.
        /// </summary>
        public static float[] Noise(float seconds, float amplitude, float cutoffHz, int seed)
        {
            int n = (int)(SampleRate * seconds);
            int fade = Mathf.Clamp(Mathf.Min(n / 8, SampleRate / 20), 1, n - 1);

            Random.State state = Random.state;
            Random.InitState(seed);

            float k = Mathf.Clamp01(cutoffHz / (SampleRate * 0.5f));
            float lp = 0f;
            float[] raw = new float[n + fade];
            for (int i = 0; i < raw.Length; i++)
            {
                lp += (Random.value * 2f - 1f - lp) * k;
                raw[i] = lp * amplitude;
            }
            Random.state = state;

            float[] data = new float[n];
            for (int i = 0; i < n; i++) data[i] = raw[i];
            // a = 0 at the seam (take the overhang, which follows data[n-1]) and 1 by the
            // end of the fade (take the head, which leads into the body of the loop).
            for (int i = 0; i < fade; i++)
            {
                float a = (float)i / fade;
                data[i] = raw[n + i] * (1f - a) + raw[i] * a;
            }
            return data;
        }

        /// <summary>A short sine blip with a soft attack and decay. The countdown beep.</summary>
        public static float[] Tone(float seconds, float hz, float amplitude)
        {
            int n = (int)(SampleRate * seconds);
            float[] data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                float env = Mathf.Min(1f, t * 25f) * Mathf.Pow(1f - t, 1.6f);
                data[i] = Mathf.Sin(i * hz * Tau / SampleRate) * env * amplitude;
            }
            return data;
        }

        /// <summary>Noise burst with a fast decay and a low thump under it.</summary>
        public static float[] BuildImpactSamples()
        {
            int n = (int)(SampleRate * 0.22f);
            float[] data = new float[n];
            Random.State state = Random.state;
            Random.InitState(97);
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                float env = Mathf.Pow(1f - t, 3.5f);
                lp += (Random.value * 2f - 1f - lp) * 0.35f;
                float thump = Mathf.Sin(i * 90f * Tau / SampleRate);
                data[i] = (lp * 0.7f + thump * 0.6f) * env * 0.9f;
            }
            Random.state = state;
            return data;
        }

        /// <summary>A rising two-note figure. Short, and the only celebratory sound.</summary>
        public static float[] BuildFinishSamples()
        {
            int n = (int)(SampleRate * 0.9f);
            float[] data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                float hz = t < 0.45f ? 660f : 990f;
                float local = t < 0.45f ? t / 0.45f : (t - 0.45f) / 0.55f;
                float env = Mathf.Min(1f, local * 18f) * Mathf.Pow(1f - local, 1.3f);
                data[i] = (Mathf.Sin(i * hz * Tau / SampleRate) * 0.6f
                         + Mathf.Sin(i * hz * 2f * Tau / SampleRate) * 0.2f) * env * 0.5f;
            }
            return data;
        }

        /// <summary>
        /// Scale to an exact peak. Doing this rather than hand-tuning a constant means the
        /// level claim in AudioProbe is a property of the clip, not of someone's arithmetic.
        /// </summary>
        public static void Normalise(float[] d, float peak)
        {
            float max = 0f;
            for (int i = 0; i < d.Length; i++) { float a = d[i] < 0f ? -d[i] : d[i]; if (a > max) max = a; }
            if (max < 1e-6f) return;
            float k = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }

        private static AudioClip Make(string name, float[] data)
        {
            AudioClip clip = AudioClip.Create(name, data.Length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
