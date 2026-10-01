using UnityEngine;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.Core
{
    /// <summary>
    /// Owns the flow, and nothing else (plan sections 4 and 7).
    ///
    /// GameDirector owns STATE TRANSITIONS. RaceSession owns RACE RULES.
    /// CarSimulation owns DRIVING. Keeping those three apart is what stops this
    /// becoming a GameManager god class.
    ///
    /// The countdown runs on a plain timer rather than a coroutine: coroutines
    /// allocate, and plan 17.5 wants zero per-frame allocation while racing.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class GameDirector : MonoBehaviour
    {
        [SerializeField] private GameConfig config;
        [SerializeField] private CarController car;
        [SerializeField] private TrackRuntime trackRuntime;

        [Tooltip("Gameplay audio. Optional - the game is designed to be fully playable " +
                 "muted (plan section 15), so a missing director is silent, not broken.")]
        [SerializeField] private RenaultRacer.Audio.AudioDirector audioDirector;

        [Header("Development")]
        [Tooltip("Skip the Ready screen and count down immediately on play. Off for the " +
                 "real flow: the Ready tap is also the browser's audio-unlock gesture, " +
                 "so skipping it leaves the game silent (plan section 15).")]
        [SerializeField] private bool autoStart = false;

        private RaceSession _session;
        private GameState _state = GameState.Boot;
        private float _countdownTimer;
        private int _lampsLit;
        private LapRejection _rejection;

        public GameState State { get { return _state; } }
        public RaceSession Session { get { return _session; } }
        public LapRejection Rejection { get { return _rejection; } }

        /// <summary>Countdown lamps currently lit, 0..3. Drives the Phase 7 lamp view.</summary>
        public int LampsLit { get { return _lampsLit; } }

        private void Start()
        {
            if (car == null || trackRuntime == null || trackRuntime.Track == null)
            {
                Debug.LogError("[GameDirector] car or track not assigned.");
                enabled = false;
                return;
            }

            float topSpeed = car.CurrentProfile != null ? car.CurrentProfile.maxSpeed : 0f;
            _session = new RaceSession(trackRuntime.Track, topSpeed);

            float floor = RaceValidator.PhysicalFloorSeconds(trackRuntime.Track, car.CurrentProfile);
            Debug.Log("[GameDirector] ready. track " + trackRuntime.Track.Count + " segments, "
                      + _session.CheckpointCount + " checkpoints, physical floor "
                      + floor.ToString("F1") + "s");

            if (audioDirector == null)
                audioDirector = FindAnyObjectByType<RenaultRacer.Audio.AudioDirector>();

            SetState(autoStart ? GameState.Countdown : GameState.Ready);
        }

        private void Update()
        {
            switch (_state)
            {
                case GameState.Ready:
                    // This tap does double duty: it starts the race AND it is the user
                    // gesture the browser requires before any audio may play. It has to
                    // stay a real tap for that reason - see AudioDirector.Unlock.
                    if (TapStarted() || Input.anyKeyDown) StartFromReady();
                    break;

                case GameState.Countdown:
                    TickCountdown();
                    break;

                case GameState.Racing:
                    TickRacing();
                    break;
            }

            // Restart is available from any resting state, and never reloads the
            // scene or the page (plan section 12).
            if (Input.GetKeyDown(KeyCode.R)) Restart();

            // A phone has no R key, and Phase 7's finish panel does not exist yet, so a
            // finished lap would otherwise be a dead end on the one device this game is
            // actually for. Once the race is over, a tap anywhere starts the next lap.
            // Gated on a short settle delay so the tap that crossed the line - or a
            // thumb still resting on a steering gutter - cannot consume the restart.
            if (_state == GameState.Finished || _state == GameState.Invalid)
            {
                _restEnded += Time.unscaledDeltaTime;
                if (_restEnded > RestartTapDelay && TapStarted()) Restart();
            }
            else _restEnded = 0f;
        }

        /// <summary>Seconds after a result before a tap is accepted as "go again".</summary>
        private const float RestartTapDelay = 0.75f;
        private float _restEnded;

        /// <summary>A finger newly pressed, or a mouse click, this frame.</summary>
        private static bool TapStarted()
        {
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
                if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
            return Input.GetMouseButtonDown(0);
        }

        private void TickCountdown()
        {
            float interval = config != null ? config.countdownLampInterval : 1f;
            _countdownTimer += Time.deltaTime;

            int lamps = Mathf.Min(3, (int)(_countdownTimer / interval));
            if (lamps != _lampsLit) _lampsLit = lamps;

            // Three lamps, then green on the fourth interval.
            if (_countdownTimer >= interval * 4f) SetState(GameState.Racing);
        }

        private void TickRacing()
        {
            // The clock advances in whole simulation steps, never Time.deltaTime, so
            // the lap time is identical at 30 and 60 FPS (plan section 12).
            float simulated = car.StepsLastFrame * car.StepDuration;
            if (simulated > 0f) _session.Tick(simulated, car.Sim.Z);

            if (_session.Finished)
            {
                _rejection = RaceValidator.Validate(_session, trackRuntime.Track,
                                                    car.CurrentProfile, config);
                SetState(_rejection == LapRejection.None ? GameState.Finished
                                                         : GameState.Invalid);
            }
        }

        /// <summary>
        /// Leave the Ready screen. Separate from <see cref="Restart"/> because this is
        /// the one transition that carries the audio unlock, and that must happen inside
        /// the user gesture rather than a frame later.
        /// </summary>
        public void StartFromReady()
        {
            if (audioDirector != null) audioDirector.Unlock();

            // The same gesture, and for the same reason. Both the audio unlock and the
            // Fullscreen API are only granted from inside a real user gesture, so the
            // Ready tap is the one moment in the whole session where either can be
            // asked for. It is best-effort: iPhone Safari does not implement fullscreen
            // for anything but a <video>, and nothing downstream may depend on it - the
            // canvas is sized from the visual viewport either way (plan 14.2).
            RenaultRacer.Web.WebViewport.RequestFullscreen();

            SetState(GameState.Countdown);
        }

        public void Restart()
        {
            car.ResetCar(0f);
            SetState(GameState.Countdown);
        }

        private void SetState(GameState next)
        {
            _state = next;

            switch (next)
            {
                case GameState.Ready:
                    FreezeCar();
                    break;

                case GameState.Countdown:
                    _countdownTimer = 0f;
                    _lampsLit = 0;
                    _rejection = LapRejection.None;
                    car.ResetCar(0f);
                    FreezeCar();
                    _session.Begin();
                    break;

                case GameState.Racing:
                    // Green. Throttle on, clock running.
                    car.Sim.ControlsEnabled = true;
                    car.Sim.ThrottleEnabled = true;
                    _session.StartRacing();
                    break;

                case GameState.Finished:
                    // Coast to a stop rather than freezing mid-air: throttle off but
                    // the sim keeps running, so the car settles naturally.
                    car.Sim.ThrottleEnabled = false;
                    car.Sim.ControlsEnabled = false;
                    Debug.Log("[GameDirector] FINISHED " + RaceSession.FormatTime(_session.ElapsedSeconds)
                              + "  checkpoints " + _session.CheckpointsPassed + "/"
                              + _session.CheckpointCount + "  session " + _session.SessionId);
                    break;

                case GameState.Invalid:
                    car.Sim.ThrottleEnabled = false;
                    car.Sim.ControlsEnabled = false;
                    Debug.LogWarning("[GameDirector] LAP REJECTED: " + _rejection
                                     + " - " + RaceValidator.Describe(_rejection));
                    break;
            }
        }

        private void FreezeCar()
        {
            car.Sim.ControlsEnabled = false;
            car.Sim.ThrottleEnabled = false;
        }
    }
}
