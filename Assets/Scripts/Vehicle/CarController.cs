using UnityEngine;
using RenaultRacer.Core;
using RenaultRacer.Track;
using RenaultRacer.Effects;
using RenaultRacer.InputSystem;

namespace RenaultRacer.Vehicle
{
    /// <summary>
    /// Scene host for <see cref="CarSimulation"/>. Steps the sim on a fixed clock,
    /// feeds it input, then pushes the result to the camera rig, the car view and
    /// the feedback effects.
    ///
    /// Thin by design: all driving behaviour lives in CarSimulation, which stays a
    /// pure, testable class with no Unity dependencies.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class CarController : MonoBehaviour
    {
        [SerializeField] private TrackRuntime trackRuntime;
        [SerializeField] private CameraRig cameraRig;
        [SerializeField] private CarView carView;
        [SerializeField] private GameConfig config;

        [Header("Input")]
        [Tooltip("Routes touch and keyboard into the one IDriveInput the sim sees " +
                 "(plan section 9). Left empty, the car falls back to keyboard only, " +
                 "so the Editor still drives if the router is missing from a scene.")]
        [SerializeField] private InputRouter inputRouter;

        [Header("Handling")]
        [Tooltip("Profiles selectable at runtime. Index 0 is the startup default.")]
        [SerializeField] private HandlingProfile[] profiles;

        [Header("Feedback")]
        [SerializeField] private FeedbackFX feedbackFX;
        [SerializeField] private SkidMarks skidMarks;

        [Tooltip("Lightning pickups. Optional; created at startup if the scene has none.")]
        [SerializeField] private BoostPickups boostPickups;

        [Tooltip("Optional. The impact is CONSUMED here - only one reader can have it - " +
                 "so audio is handed a copy rather than polling the simulation itself.")]
        [SerializeField] private RenaultRacer.Audio.AudioDirector audioDirector;

        private CarSimulation _sim;
        private FixedStepClock _clock;
        private IDriveInput _input;
        private int _profileIndex;

        public CarSimulation Sim { get { return _sim; } }
        public BoostPickups Pickups { get { return boostPickups; } }

        /// <summary>Pickups taken since the last reset. GameUi and audio watch it go up.</summary>
        public int BoostPickupsCollected { get; private set; }

        /// <summary>
        /// Curve strength under the car, or 0 before the simulation exists. Used by
        /// QualityDirector to find a straight to change tier on.
        /// </summary>
        public float CurrentCurve { get { return _sim != null ? _sim.CurrentCurve : 0f; } }
        public HandlingProfile CurrentProfile
        {
            get { return (profiles != null && profiles.Length > 0) ? profiles[_profileIndex] : null; }
        }
        public string CurrentProfileName
        {
            get { return CurrentProfile != null ? CurrentProfile.name : "none"; }
        }
        public int ProfileCount { get { return profiles != null ? profiles.Length : 0; } }
        public int ProfileIndex { get { return _profileIndex; } }

        /// <summary>
        /// Fixed steps simulated during the last Update. RaceSession multiplies this
        /// by StepDuration so the lap clock advances in whole simulation steps and is
        /// identical at 30 and 60 FPS (plan section 12).
        /// </summary>
        public int StepsLastFrame { get; private set; }

        /// <summary>Duration of one fixed simulation step, seconds.</summary>
        public float StepDuration { get { return _clock != null ? _clock.Step : 1f / 60f; } }

        /// <summary>
        /// Hold the simulation entirely. Used by the rotate gate: no steps run, so
        /// StepsLastFrame is 0 and the lap clock - which advances from simulated steps,
        /// not from Time.deltaTime - stops with it. Time.timeScale is deliberately not
        /// used; it would also freeze the unscaled UI animation that is telling the
        /// player what to do.
        /// </summary>
        public bool Paused
        {
            get { return _paused; }
            set
            {
                if (_paused == value) return;
                _paused = value;
                if (boostPickups != null) boostPickups.Frozen = value;
                if (feedbackFX != null) feedbackFX.SetFrozen(value);
                // Drop the accumulator so resuming does not replay the paused seconds.
                if (!_paused && _clock != null) _clock.Reset();
                StepsLastFrame = 0;
            }
        }
        private bool _paused;

        private void Start()
        {
            if (trackRuntime == null || trackRuntime.Track == null)
            {
                Debug.LogError("[CarController] no TrackRuntime/Track.");
                enabled = false;
                return;
            }
            if (profiles == null || profiles.Length == 0 || profiles[0] == null)
            {
                Debug.LogError("[CarController] no HandlingProfile assigned.");
                enabled = false;
                return;
            }

            float step = config != null ? config.simulationStep : 1f / 60f;
            int maxSteps = config != null ? config.maxSimulationSteps : 4;

            _profileIndex = 0;
            _sim = new CarSimulation(trackRuntime.Track, profiles[0]);
            _sim.Reset(0f);
            _clock = new FixedStepClock(step, maxSteps);
            _input = inputRouter != null ? (IDriveInput)inputRouter : new KeyboardDriveInput();

            // Lightning pickups: drawn through the road's own samples, swept here once per
            // simulation step. Made at runtime if the scene has none, so no scene edit is
            // needed to have them - at the origin, unit scale, which is what they assume.
            if (boostPickups == null) boostPickups = FindAnyObjectByType<BoostPickups>();
            if (boostPickups == null)
            {
                var go = new GameObject("BoostPickups");
                go.transform.position = Vector3.zero;
                boostPickups = go.AddComponent<BoostPickups>();
            }

            // Phase 10: rubber goes down under the drawn tyres, and marks are drawn out to
            // wherever the car is drawn. Presentation values only.
            if (skidMarks != null && carView != null)
            {
                skidMarks.wheelOffset = carView.RearTrackHalfWidthNormalised;
                skidMarks.maxDrawDistance = carView.distanceAhead + 100f;
            }

            Debug.Log("[CarController] ready. step=" + step.ToString("F4")
                      + " maxSteps=" + maxSteps + " profiles=" + profiles.Length
                      + " active=" + profiles[0].name
                      + " input=" + (inputRouter != null ? "InputRouter" : "keyboard only"));
        }

        private void Update()
        {
            if (_sim == null) return;

            if (_paused)
            {
                StepsLastFrame = 0;
                return;
            }

            float dt = Time.deltaTime;

            // Simulation advances only in whole fixed steps, so behaviour is identical
            // at 30 and 60 FPS.
            int steps = _clock.Advance(dt);
            StepsLastFrame = steps;
            float ahead = carView != null ? carView.distanceAhead : 1400f;
            for (int i = 0; i < steps; i++)
            {
                float zFrom = _sim.Z + ahead, xFrom = _sim.X;
                _sim.Step(_clock.Step, _input);
                // Swept where the car is DRAWN, so it is taken when it is seen to be hit.
                if (boostPickups != null && _sim.ControlsEnabled
                    && boostPickups.Sweep(_sim, zFrom, _sim.Z + ahead, xFrom, _sim.X))
                {
                    // The ONE collection event: BoostPickups has hidden the bolt, handed
                    // the boost over and started the arc burst; AudioDirector hears this
                    // count go up and plays the zap. (FeedbackFX.BoostBurstAt is no longer
                    // called - the arcs replace that generic spark burst.)
                    BoostPickupsCollected++;
                }
            }

            // Smoke and skid marks read SLIP, not lateral velocity: tyres only mark
            // the road when they are scrubbing, and a car crossing the road diagonally
            // but rolling true is not. maxLateralVel is a sanity bound now, so the old
            // ratio no longer meant anything (plan 8.16).
            float lateralSlip = _sim.SlipRatio;
            float distanceAhead = carView != null ? carView.distanceAhead : 1400f;

            float impact = _sim.ConsumeImpact();
            if (impact > 0f)
            {
                if (cameraRig != null) cameraRig.Impact(impact);
                if (feedbackFX != null)
                {
                    // At the drawn car, not at a road point: the car is seated on its own
                    // piece of road (plan 10.1), so on a hill the projected road point
                    // ahead is not where the car is drawn.
                    //
                    // And on the side that was actually hit. The simulation resolves the
                    // barrier by side already, so the side is free information; without
                    // it every scrape sparked out of the car's centre line, which reads
                    // as the car breaking rather than as the wall being scraped.
                    if (carView != null && carView.HasDirectionalArt)
                    {
                        int side = _sim.ImpactSide < 0f ? -1 : 1;
                        feedbackFX.ImpactAt(impact, carView.BodyFlank(side));
                    }
                    else
                        feedbackFX.Impact(impact, _sim.X, distanceAhead);
                }
                if (audioDirector != null) audioDirector.ReportImpact(impact);
            }

            if (cameraRig != null)
                cameraRig.Follow(_sim.Z, _sim.X, _sim.SpeedPct,
                                 _sim.LateralVel, _sim.CurrentCurve, dt);

            if (carView != null)
                // Presentation reads the steering and brake the SIMULATION acts on - so
                // with controls locked for the countdown the car shows straight and dark
                // brake lights - plus the body's heading for the alternative view mode.
                carView.UpdateView(_sim.X, _sim.ControlsEnabled ? _input.SteerAxis : 0f,
                                   _sim.Yaw, _sim.YawRate,
                                   _sim.ControlsEnabled && _input.BrakeHeld, dt);

            if (feedbackFX != null)
            {
                if (carView != null && carView.HasDirectionalArt)
                    feedbackFX.UpdateSmokeAt(carView.RearWheel(-1), carView.RearWheel(1),
                                             _sim.DriftAmount, lateralSlip, _sim.SpeedPct);
                else
                    feedbackFX.UpdateSmoke(_sim.X, _sim.DriftAmount, lateralSlip,
                                           _sim.SpeedPct, distanceAhead);
            }

            if (skidMarks != null)
            {
                // Marks go down where the car is SEEN, not where it is simulated.
                //
                // In a pseudo-3D racer those are different places: CameraRig puts the
                // camera at the car's own z and CarView draws the car distanceAhead in
                // front of it, which is what makes the chase view. Recording at _sim.Z
                // therefore laid every mark exactly AT the camera, so every one of them
                // failed the near-plane test in Rebuild and NOTHING was ever drawn -
                // which is why no skid mark has been visible in any build to date.
                // FeedbackFX already had this right for the smoke (it passes
                // distanceAhead); the skid marks were the one effect that did not.
                skidMarks.Record(_sim.X, _sim.Z + distanceAhead, lateralSlip,
                                 _sim.DriftAmount, _sim.SpeedPct, _sim.OffRoad, _sim.BrakeSkid);
                skidMarks.Rebuild(distanceAhead);
            }
        }

        /// <summary>
        /// Swap handling profile in place, keeping the car's current state. Lets a
        /// tester feel the difference mid-corner instead of restarting between runs.
        /// </summary>
        public void SetProfile(int index)
        {
            if (profiles == null || profiles.Length == 0 || _sim == null) return;
            _profileIndex = ((index % profiles.Length) + profiles.Length) % profiles.Length;
            _sim.Profile = profiles[_profileIndex];
            Debug.Log("[CarController] handling profile -> " + profiles[_profileIndex].name);
        }

        public void NextProfile() { SetProfile(_profileIndex + 1); }

        /// <summary>Full reset in place - no scene reload (plan section 12).</summary>
        public void ResetCar(float startZ)
        {
            if (_sim == null) return;
            _sim.Reset(startZ);
            _clock.Reset();
            StepsLastFrame = 0;
            if (cameraRig != null) cameraRig.ResetRig();
            if (carView != null) carView.ResetView();
            if (feedbackFX != null) feedbackFX.ClearAll();
            if (skidMarks != null) skidMarks.Clear();
            if (boostPickups != null) boostPickups.ResetAll();
            BoostPickupsCollected = 0;
        }
    }
}
