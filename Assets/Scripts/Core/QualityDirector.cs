using UnityEngine;
using RenaultRacer.Effects;
using RenaultRacer.Track;
using RenaultRacer.UI;
using RenaultRacer.Vehicle;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace RenaultRacer.Core
{
    /// <summary>
    /// Adaptive quality (plan section 20).
    ///
    /// The whole design is in one sentence: **it only ever steps down, at most once, and
    /// never while the player is busy.** Quality that oscillates is worse than quality
    /// that is simply low - a frame-rate dip that triggers a downgrade frees up headroom,
    /// which triggers an upgrade, which causes the dip again, and the player watches the
    /// game breathe. A one-way ratchet cannot do that.
    ///
    /// It measures during the first seconds of RACING, not during boot or the countdown.
    /// Boot is dominated by decompression and shader warm-up and says nothing about how
    /// the game will run; the countdown has no road moving. The first straight does.
    ///
    /// The starting tier is guessed from the device, so a 2 GB phone does not have to
    /// suffer a full-quality opening straight before anything happens - but the guess is
    /// only ever a starting point. The measurement is what decides.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class QualityDirector : MonoBehaviour
    {
        public enum Tier
        {
            /// <summary>iPhone 12+, Pixel 6+, desktop. 60 FPS, everything on.</summary>
            Ideal = 0,
            /// <summary>iPhone SE2/XR, Galaxy A54, Redmi Note 11. 50-60 FPS.</summary>
            Normal = 1,
            /// <summary>Redmi 9A, Galaxy A03 Core, Moto E20. 30+ FPS, effects pared back.</summary>
            Weak = 2
        }

        [Header("Wiring")]
        [SerializeField] private GameDirector director;
        [SerializeField] private RoadRenderer roadRenderer;
        [SerializeField] private FeedbackFX feedbackFX;
        [SerializeField] private SkidMarks skidMarks;
        [Tooltip("Optional. SpeedLines is built at runtime by HudView, so it cannot be " +
                 "wired in the Inspector - it is resolved on the first apply after the " +
                 "HUD exists.")]
        [SerializeField] private SpeedLines speedLines;
        [SerializeField] private RoadsideSprites roadsideSprites;
        [SerializeField] private CarController car;

        [Header("Measurement")]
        [Tooltip("Seconds of RACING to average before deciding. Long enough to cross a " +
                 "corner and a straight, short enough that a weak device is not left at " +
                 "full quality for a meaningful part of a 40-second lap.")]
        [Range(1f, 15f)] public float sampleSeconds = 5f;

        [Tooltip("Frames thrown away at the start of the window. The first frames of " +
                 "racing include the countdown teardown and the first touch of every " +
                 "shader variant, and they are not representative of anything.")]
        [Range(0, 60)] public int warmupFrames = 20;

        [Tooltip("Average frame time, milliseconds, above which the tier steps down. " +
                 "28 ms is about 36 FPS - comfortably below the 45 FPS a Normal device " +
                 "should manage and comfortably above the 30 FPS a Weak one must.")]
        [Range(16f, 60f)] public float stepDownMilliseconds = 28f;

        [Tooltip("Curve magnitude under the car below which a downgrade may be applied. " +
                 "A tier change costs a frame or two; spending them mid-corner is exactly " +
                 "when the player can least afford it.")]
        [Range(0f, 6f)] public float quietCurve = 1.2f;

        [Tooltip("Seconds to wait for a quiet moment before applying the downgrade anyway. " +
                 "A lap can be one long corner; the downgrade must not be postponed forever.")]
        [Range(0.5f, 10f)] public float quietTimeout = 4f;

        [Header("Tier settings")]
        [Tooltip("Device pixel ratio cap per tier. THE single biggest mobile lever: a " +
                 "cheap phone at DPR 3 renders nine times the pixels of DPR 1 onto a panel " +
                 "that cannot show the difference, and this game is fill-rate bound.")]
        public float[] maxDpr = { 2.0f, 1.5f, 1.0f };

        [Tooltip("Segments of road drawn ahead of the camera, per tier.")]
        public int[] drawDistance = { 300, 260, 180 };

        [Tooltip("Smoke particle cap per tier.")]
        public int[] smokeParticles = { 20, 20, 10 };

        [Tooltip("Roadside sprites drawn per tier (0 = none). Props are off for V1 " +
                 "regardless; this is the cap they will honour when Phase 10 turns them on.")]
        public int[] roadsideSprites_ = { 80, 60, 24 };

        [Tooltip("Skid marks per tier. ON EVERYWHERE. Plan 20 had them off on Weak, and " +
                 "for as long as every WebGL device was MISCLASSIFIED as Weak that single " +
                 "false was the whole reason the effect had never been seen outside the " +
                 "Editor. With the classification fixed the question is worth asking on " +
                 "its merits, and the answer is still yes: the trail is a handful of " +
                 "quads in one batch on one material, it is bounded by markLimit rather " +
                 "than growing with the lap, and it is one of the few things on screen " +
                 "that tells the player the car is doing something. The draw distance and " +
                 "the device pixel ratio are the levers that actually buy frames.")]
        public bool[] skidMarksOn = { true, true, true };

        [Tooltip("Speed lines per tier. ON EVERYWHERE, unlike the rest of this table. " +
                 "Plan 20 had them off on Weak with the skid marks, but they are not the " +
                 "same kind of cost: skid marks are an unbounded trail mesh that grows " +
                 "for a whole lap, while the streaks are fourteen UI quads in one batch " +
                 "that between them cover a few per cent of the frame. Weak is also the " +
                 "tier a browser drops to, which is how the effect came to be invisible " +
                 "in the very sessions that went looking for it - and a phone that has " +
                 "stepped down is exactly the device with the weakest sense of speed to " +
                 "begin with.")]
        public bool[] speedLinesOn = { true, true, true };

        // ---- state ----
        private Tier _tier = Tier.Ideal;
        private bool _decided;
        private bool _pendingStepDown;
        private float _pendingSince;
        private float _accumulated;
        private int _sampled;
        private int _warmupLeft;
        private bool _measuring;
        private bool _reappliedForRuntimeSystems;

        /// <summary>The tier currently applied.</summary>
        public Tier CurrentTier { get { return _tier; } }

        /// <summary>The tier guessed at boot, before anything was measured.</summary>
        public Tier BootTier { get; private set; }

        /// <summary>Average frame time over the measurement window, ms. 0 until measured.</summary>
        public float MeasuredMilliseconds { get; private set; }

        /// <summary>True once the one permitted decision has been taken.</summary>
        public bool Decided { get { return _decided; } }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void RRSetMaxDpr(float cap);
        [DllImport("__Internal")] private static extern float RRDeviceMemoryGb();
        [DllImport("__Internal")] private static extern int RRHardwareConcurrency();
#endif

        /// <summary>
        /// What the device said about itself, for the log and the debug HUD. Set by
        /// <see cref="GuessTierFromDevice"/>.
        /// </summary>
        public static string DeviceEvidence { get; private set; }

        private void Awake()
        {
            BootTier = GuessTierFromDevice();
            _tier = BootTier;
            Apply(_tier);

            Debug.Log("[QualityDirector] boot tier " + _tier
                      + "   " + DeviceEvidence
                      + " gfx=" + SystemInfo.graphicsDeviceType
                      + " maxTex=" + SystemInfo.maxTextureSize);
        }

        /// <summary>
        /// A starting point, not a verdict. Deliberately coarse and deliberately
        /// optimistic: guessing too HIGH costs a few seconds of measurement, guessing too
        /// low would leave a capable phone permanently degraded, and the measurement can
        /// only ever step down.
        ///
        /// SystemInfo.systemMemorySize IS NOT THE DEVICE'S RAM ON WEBGL, and reading it as
        /// though it were is the bug this method was rewritten to remove. On WebGL it
        /// reports the WebAssembly heap - a number chosen in this project's own player
        /// settings, identical on a Redmi 9A and on a desktop workstation, and at or under
        /// 2048 MB. So the first line of the old version classified EVERY WebGL device as
        /// Weak, on every phone and on every desktop browser. Weak had the skid marks
        /// switched off, which is why no build has ever shown one outside the Editor.
        ///
        /// So on WebGL the heap is not consulted at all. The browser is asked instead, and
        /// where it declines to answer - iOS Safari and Firefox do not implement
        /// deviceMemory - the answer is Ideal and the frame-time measurement decides. Not
        /// knowing is not evidence of being slow.
        /// </summary>
        private static Tier GuessTierFromDevice()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            float gb = 0f;
            int cores = 0;
            try { gb = RRDeviceMemoryGb(); cores = RRHardwareConcurrency(); }
            catch (System.Exception) { }

            DeviceEvidence = "browser deviceMemory=" + (gb > 0f ? gb.ToString("0.##") + " GB" : "unstated")
                           + " cores=" + (cores > 0 ? cores.ToString() : "unstated")
                           + "   (the WASM heap, " + SystemInfo.systemMemorySize
                           + " MB, is deliberately ignored)";

            if (gb > 0f && gb <= 2f) return Tier.Weak;
            if (gb > 0f && gb <= 3f) return Tier.Normal;
            if (cores > 0 && cores <= 2) return Tier.Normal;
            return Tier.Ideal;
#else
            int mb = SystemInfo.systemMemorySize;
            int cpus = SystemInfo.processorCount;
            DeviceEvidence = "systemMemorySize=" + mb + " MB cpus=" + cpus;

            if (mb > 0 && mb <= 2048) return Tier.Weak;
            if (mb > 0 && mb <= 3072) return Tier.Normal;
            if (cpus > 0 && cpus <= 2) return Tier.Normal;
            return Tier.Ideal;
#endif
        }

        private void Update()
        {
            if (_decided && !_pendingStepDown) return;
            if (director == null) return;

            bool racing = director.State == GameState.Racing;

            // Some of what a tier governs does not exist at Awake: HudView builds the
            // speed lines when the HUD is first shown. Re-apply once the race is running
            // and everything is real, or a Weak device would keep the one effect that was
            // created after the decision was taken.
            if (racing && !_reappliedForRuntimeSystems)
            {
                _reappliedForRuntimeSystems = true;
                Apply(_tier);
            }

            if (!_decided)
            {
                if (!racing)
                {
                    // The window only runs while the road is actually moving. Restarting
                    // begins it again from scratch.
                    _measuring = false;
                    return;
                }

                if (!_measuring)
                {
                    _measuring = true;
                    _warmupLeft = warmupFrames;
                    _accumulated = 0f;
                    _sampled = 0;
                }

                if (_warmupLeft > 0) { _warmupLeft--; return; }

                _accumulated += Time.unscaledDeltaTime;
                _sampled++;

                if (_accumulated >= sampleSeconds)
                {
                    MeasuredMilliseconds = _accumulated / Mathf.Max(1, _sampled) * 1000f;
                    _decided = true;

                    if (MeasuredMilliseconds > stepDownMilliseconds && _tier != Tier.Weak)
                    {
                        _pendingStepDown = true;
                        _pendingSince = Time.unscaledTime;
                        Debug.Log("[QualityDirector] " + MeasuredMilliseconds.ToString("F1")
                                  + " ms/frame over " + _sampled + " frames - stepping down from "
                                  + _tier + ", waiting for a quiet moment");
                    }
                    else
                    {
                        Debug.Log("[QualityDirector] " + MeasuredMilliseconds.ToString("F1")
                                  + " ms/frame over " + _sampled + " frames - staying on " + _tier);
                    }
                }
                return;
            }

            // A downgrade is decided but not yet applied: wait for somewhere it will not
            // be felt. Straight road, or the timeout, or the race ending.
            if (_pendingStepDown)
            {
                bool quiet = car == null
                          || Mathf.Abs(car.CurrentCurve) <= quietCurve
                          || !racing;
                bool waitedLongEnough = Time.unscaledTime - _pendingSince >= quietTimeout;
                if (quiet || waitedLongEnough)
                {
                    _pendingStepDown = false;
                    _tier = (Tier)Mathf.Min((int)_tier + 1, (int)Tier.Weak);
                    Apply(_tier);
                    Debug.Log("[QualityDirector] now on " + _tier
                              + (quiet ? "   (applied on a quiet stretch)"
                                       : "   (applied on the timeout - the lap never went quiet)"));
                }
            }
        }

        /// <summary>
        /// Push a tier into every system it governs. Safe to call with anything missing:
        /// each knob is independent, and a scene that has not wired one up should lose
        /// that knob rather than the whole mechanism.
        /// </summary>
        public void Apply(Tier tier)
        {
            int i = Mathf.Clamp((int)tier, 0, 2);
            ResolveLateWiring();

            if (roadRenderer != null && drawDistance != null && i < drawDistance.Length)
                roadRenderer.drawDistance = drawDistance[i];

            if (feedbackFX != null && smokeParticles != null && i < smokeParticles.Length)
                feedbackFX.SetSmokeCap(smokeParticles[i]);

            if (skidMarks != null && skidMarksOn != null && i < skidMarksOn.Length)
            {
                if (!skidMarksOn[i]) skidMarks.Clear();
                skidMarks.enabled = skidMarksOn[i];
                // Disabling the component stops Record/Rebuild being useful, but the mesh
                // it already uploaded would otherwise stay on screen forever.
                MeshRenderer mr = skidMarks.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = skidMarksOn[i];
            }

            if (speedLines != null && speedLinesOn != null && i < speedLinesOn.Length)
                speedLines.SetEnabled(speedLinesOn[i]);

            if (roadsideSprites != null && roadsideSprites_ != null && i < roadsideSprites_.Length)
                roadsideSprites.SetVisibleCap(roadsideSprites_[i]);

            SetDevicePixelRatioCap(maxDpr != null && i < maxDpr.Length ? maxDpr[i] : 2f);
        }

        /// <summary>
        /// Fill in anything the Inspector could not, because it is built at runtime.
        /// Called from Apply, which happens two or three times in a session, so the
        /// search cost is irrelevant and the alternative - a registry, or HudView
        /// knowing about quality tiers - would be worse for a single reference.
        /// </summary>
        private void ResolveLateWiring()
        {
            if (speedLines == null)
                speedLines = Object.FindAnyObjectByType<SpeedLines>(FindObjectsInactive.Include);
        }

        private static void SetDevicePixelRatioCap(float cap)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { RRSetMaxDpr(cap); }
            catch (System.EntryPointNotFoundException)
            {
                // Hosted by a page that is not our template. Everything else still applies.
            }
#endif
        }

        /// <summary>
        /// Force a tier. Development only - this is how a tier gets LOOKED at on a real
        /// device rather than reasoned about.
        /// </summary>
        public void ForceTier(Tier tier)
        {
            _tier = tier;
            _decided = true;
            _pendingStepDown = false;
            Apply(tier);
            Debug.Log("[QualityDirector] forced to " + tier);
        }
    }
}
