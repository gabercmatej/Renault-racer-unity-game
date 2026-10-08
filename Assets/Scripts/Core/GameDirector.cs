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

        // --- holds ------------------------------------------------------------------
        // ONE owner for "the game is held". Two independent reasons can hold it - the
        // player pressed pause, or the portrait rotate gate is up - and they are combined
        // here (held = user || gate) so that neither one's release can clear the other's.
        // Held means: the simulation is paused (CarController.Paused, which also stops the
        // lap clock because that advances from simulated steps), driving input is
        // suspended, the countdown and the post-finish window stop, and the gameplay
        // loops duck out (AudioDirector). Time.timeScale is never touched.
        //
        // A third, separate reason stops the SIMULATION without holding the game: the
        // finish settle (see PostFinishSeconds). It is not a pause - nothing waits for it
        // to end except a restart - so it never touches input or the pause button.
        //
        // A page that went hidden mid-race (app switch, lock screen, tab change) comes
        // back as a USER pause: the browser stopped the loop anyway, and the player should
        // return to PREMOR rather than straight into a corner at speed.
        private bool _userPaused;
        private bool _gateHeld;
        private bool _settled;
        private float _postFinish;
        private int _pageHideCount;
        private RenaultRacer.InputSystem.InputRouter _input;

        /// <summary>The player paused the game (pause button or Esc).</summary>
        public bool UserPaused { get { return _userPaused; } }

        /// <summary>The portrait rotate gate is up.</summary>
        public bool GateHeld { get { return _gateHeld; } }

        /// <summary>Either reason holds the game.</summary>
        public bool Held { get { return _userPaused || _gateHeld; } }

        /// <summary>
        /// Seconds of simulated driving after the line before the car is stopped for good.
        /// Counted in simulation steps, so a rotate gate (or anything else that pauses the
        /// sim) suspends the window rather than letting it expire behind the overlay.
        /// </summary>
        public const float PostFinishSeconds = 3f;

        /// <summary>
        /// Extra coast deceleration after the line, fraction of maxSpeed per second, on top
        /// of the profile's coastDrag. Brings a car that crossed at top speed (even boosted)
        /// to rest within about PostFinishSeconds, so the stop reads as the car pulling up,
        /// not as the game freezing mid-roll.
        /// </summary>
        public const float FinishDecel = 0.33f;

        /// <summary>The finish window has run out and the simulation is stopped until a restart.</summary>
        public bool Settled { get { return _settled; } }

        /// <summary>Simulated seconds since the line, 0 before it. For probes.</summary>
        public float PostFinishElapsed { get { return _postFinish; } }

        /// <summary>The player may pause now: countdown or racing, and the gate is not up.</summary>
        public bool CanUserPause
        {
            get { return !_gateHeld && (_state == GameState.Countdown || _state == GameState.Racing); }
        }

        /// <summary>Pause or resume on the player's behalf. Ignored where pausing makes no sense.</summary>
        public void SetUserPaused(bool paused)
        {
            if (paused && !CanUserPause) return;
            if (_userPaused == paused) return;
            _userPaused = paused;
            ApplyHold();
        }

        /// <summary>The rotate gate opening or closing. Never touches a user pause.</summary>
        public void SetGateHeld(bool gated)
        {
            if (_gateHeld == gated) return;
            _gateHeld = gated;
            ApplyHold();
        }

        private void ApplyHold()
        {
            bool held = Held;
            if (car != null) car.Paused = held || _settled;
            if (_input == null) _input = FindAnyObjectByType<RenaultRacer.InputSystem.InputRouter>();
            if (_input != null) _input.Suspend(held);
        }

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

                case GameState.Finished:
                case GameState.Invalid:
                    TickPostFinish();
                    break;
            }

            // The page was hidden since the last frame: pause on the player's behalf.
            int hides = RenaultRacer.Audio.AudioUnlockBridge.PageHideCount;
            if (hides != _pageHideCount)
            {
                _pageHideCount = hides;
                if (CanUserPause && !_userPaused)
                {
                    SetUserPaused(true);
                    Debug.Log("[GameDirector] page was hidden - paused");
                }
            }

            // Esc toggles the pause. (P belongs to the debug panel's profile keys.)
            if (Input.GetKeyDown(KeyCode.Escape) && (_userPaused || CanUserPause))
                SetUserPaused(!_userPaused);

            // Restart is available from any resting state, and never reloads the
            // scene or the page (plan section 12). Not behind the rotate gate: nothing
            // may change state while the player cannot see the game.
            if (Input.GetKeyDown(KeyCode.R) && !_gateHeld) Restart();

            // A phone has no R key, and Phase 7's finish panel does not exist yet, so a
            // finished lap would otherwise be a dead end on the one device this game is
            // actually for. Once the race is over, a tap anywhere starts the next lap.
            // Gated on a short settle delay so the tap that crossed the line - or a
            // thumb still resting on a steering gutter - cannot consume the restart.
            //
            // While the rotate gate is up the result waits too: no settle time passes and
            // no tap behind the overlay can restart. A tap while the finish card is still
            // revealing completes the reveal instead of restarting, so a player who taps to
            // see the numbers sooner does not lose them - unless it lands on the button.
            if (_state == GameState.Finished || _state == GameState.Invalid)
            {
                if (!_gateHeld) _restEnded += Time.unscaledDeltaTime;
                if (_restEnded > RestartTapDelay && !_gateHeld && TapStarted()
                    && !RenaultRacer.UI.ResultsReveal.ConsumeTapAsSkip())
                    Restart();
            }
            else _restEnded = 0f;
        }

        /// <summary>Seconds after a result before a tap is accepted as "go again".</summary>
        private const float RestartTapDelay = 0.75f;
        private float _restEnded;

        /// <summary>A finger newly pressed, or a mouse click, this frame.</summary>
        private static bool TapStarted()
        {
            // A press on an interactive HUD control (sound, pause, resume) is that
            // control's, not "start/restart".
            int n = Input.touchCount;
            for (int i = 0; i < n; i++)
            {
                Touch t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && !RenaultRacer.UI.HudControls.IsOverInteractive(t.position)) return true;
            }
            return n == 0 && Input.GetMouseButtonDown(0)
                   && !RenaultRacer.UI.HudControls.IsOverInteractive(Input.mousePosition);
        }

        private void TickCountdown()
        {
            if (Held) return;   // paused: the lamps wait too
            float interval = config != null ? config.countdownLampInterval : 1f;
            _countdownTimer += Time.deltaTime;

            int lamps = Mathf.Min(3, (int)(_countdownTimer / interval));
            if (lamps != _lampsLit) _lampsLit = lamps;

            // Three lamps, then green on the fourth interval.
            if (_countdownTimer >= interval * 4f) SetState(GameState.Racing);
        }

        /// <summary>
        /// The car rolls on after the line, then stops. The lap time was locked on the tick
        /// that crossed it (RaceSession stops running once Finished), so nothing here can
        /// change it, add a lap, or collect a pickup (CarController sweeps only while
        /// controls are on, and they went off with the throttle).
        /// </summary>
        private void TickPostFinish()
        {
            if (_settled) return;
            _postFinish += car.StepsLastFrame * car.StepDuration;
            if (_postFinish >= PostFinishSeconds)
            {
                _settled = true;
                ApplyHold();
                Debug.Log("[GameDirector] finish settle - simulation stopped, speed "
                          + car.Sim.Speed.ToString("F0"));
            }
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
                                                    car.CurrentProfile, config,
                                                    car.Pickups != null ? car.Pickups.MaxSecondsSaved : 0f);
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
            if (_userPaused) { _userPaused = false; ApplyHold(); }
            car.ResetCar(0f);
            SetState(GameState.Countdown);
        }

        private void SetState(GameState next)
        {
            _state = next;

            // Every state change starts the finish window afresh (only Finished and Invalid
            // run it), and a restart must lift a settled car.
            _postFinish = 0f;
            if (_settled) { _settled = false; ApplyHold(); }

            // A user pause only exists during the countdown and the race.
            if (_userPaused && next != GameState.Countdown && next != GameState.Racing)
            { _userPaused = false; ApplyHold(); }

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
                    // Coast to a stop rather than freezing mid-air: throttle off plus a
                    // gentle extra drag, and the sim keeps running for PostFinishSeconds so
                    // the car pulls up before TickPostFinish stops it.
                    car.Sim.ThrottleEnabled = false;
                    car.Sim.ControlsEnabled = false;
                    car.Sim.FinishDecel = FinishDecel;
                    Debug.Log("[GameDirector] FINISHED " + RaceSession.FormatTime(_session.ElapsedSeconds)
                              + "  checkpoints " + _session.CheckpointsPassed + "/"
                              + _session.CheckpointCount + "  session " + _session.SessionId);
                    break;

                case GameState.Invalid:
                    car.Sim.ThrottleEnabled = false;
                    car.Sim.ControlsEnabled = false;
                    car.Sim.FinishDecel = FinishDecel;
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
