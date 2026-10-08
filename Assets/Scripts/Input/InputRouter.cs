using UnityEngine;

namespace RenaultRacer.InputSystem
{
    /// <summary>
    /// The one input the simulation sees (plan section 9).
    ///
    /// Touch and keyboard both run, always, and are merged here. That is deliberate:
    /// a phone that reports a keyboard, a laptop with a touch screen, and a developer
    /// driving the Web build with arrow keys to reproduce a phone bug all work without
    /// a device-detection branch - and device detection on mobile web is a guessing
    /// game we do not need to play.
    ///
    /// Nothing downstream knows this class exists. CarSimulation takes an IDriveInput
    /// and never learns whether a thumb or a key produced it, which is what lets the
    /// touch layout be retuned on a phone without touching the handling model.
    ///
    /// Runs before CarController (-100), so the frame's input is polled once, early,
    /// and every reader in the frame sees the same values.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class InputRouter : MonoBehaviour, IDriveInput
    {
        [Header("Touch layout (placeholder geometry - plan section 9)")]
        [Tooltip("Where the two steering gutters meet, as a fraction of screen width.")]
        [Range(0.3f, 0.7f)] public float steerSplit = 0.5f;

        [Tooltip("Brake button size as a fraction of the shorter screen edge.")]
        [Range(0.12f, 0.45f)] public float brakeSize = 0.30f;

        [Tooltip("Brake button margin in from the safe-area corner, same units.")]
        [Range(0f, 0.2f)] public float brakeMargin = 0.05f;

        [Tooltip("Bottom-LEFT brake button (the M6 device-test choice) rather than " +
                 "bottom-right. The button always wins a touch that starts inside it, " +
                 "so this decides which steering direction needs a second finger to " +
                 "brake with - it does not remove that cost. Plan 9.1.")]
        public bool brakeOnLeft = true;

        [Tooltip("How long brake and steer must be held together to mean 'drift'. " +
                 "Stops an ordinary trail-braked corner from provoking a slide.")]
        [Range(0f, 0.6f)] public float handbrakeHoldSeconds = 0.18f;

        [Tooltip("Let the mouse act as one finger, for the Editor and desktop browsers.")]
        public bool emulateTouchWithMouse = true;

        [Header("Sources")]
        [Tooltip("Keyboard stays live in every build - it is how the game is developed.")]
        public bool keyboardEnabled = true;

        [Tooltip("Touch stays live in every build; it costs nothing where there is none.")]
        public bool touchEnabled = true;

        private IDriveInput _keyboard = new KeyboardDriveInput();   // interface so a probe can hold a "key"
        private readonly TouchDriveInput _touch = new TouchDriveInput();

        private float _steer;
        private bool _brake;
        private bool _handbrake;

        /// <summary>The touch source, for the placeholder control view to draw from.</summary>
        public TouchDriveInput Touch { get { return _touch; } }

        /// <summary>True while input is ignored entirely - the rotate gate holds this.</summary>
        public bool Suspended { get; private set; }

        /// <summary>
        /// True after a suspension ends, until every finger and key has been released:
        /// output stays at zero so a steer or brake held through a pause (or a rotate)
        /// cannot jolt the car the instant control returns.
        /// </summary>
        public bool AwaitingRelease { get; private set; }

        public float SteerAxis { get { return _steer; } }
        public bool BrakeHeld { get { return _brake; } }
        public bool HandbrakeHeld { get { return _handbrake; } }

        /// <summary>Which source produced the current frame's steering. Debug only.</summary>
        public bool SteeringFromTouch { get; private set; }

        private void Awake()
        {
            PushTuning();
        }

        private void OnValidate()
        {
            PushTuning();
        }

        private void PushTuning()
        {
            _touch.steerSplit = steerSplit;
            _touch.brakeSize = brakeSize;
            _touch.brakeMargin = brakeMargin;
            _touch.brakeOnLeft = brakeOnLeft;
            _touch.handbrakeHoldSeconds = handbrakeHoldSeconds;
            _touch.emulateWithMouse = emulateTouchWithMouse;
        }

        private void Update()
        {
            if (Suspended)
            {
                _steer = 0f;
                _brake = false;
                _handbrake = false;
                SteeringFromTouch = false;
                return;
            }

            float touchSteer = 0f;
            bool touchBrake = false, touchHandbrake = false;

            if (touchEnabled)
            {
                _touch.Poll(Time.unscaledDeltaTime);
                touchSteer = _touch.SteerAxis;
                touchBrake = _touch.BrakeHeld;
                touchHandbrake = _touch.HandbrakeHeld;
            }

            float keySteer = keyboardEnabled ? _keyboard.SteerAxis : 0f;

            // Touch wins when a thumb is actually asking for something; otherwise the
            // keyboard has it. Merging by "whichever is non-zero" rather than by device
            // detection means neither can lock the other out.
            SteeringFromTouch = touchSteer != 0f;
            _steer = SteeringFromTouch ? touchSteer : keySteer;

            _brake = touchBrake || (keyboardEnabled && _keyboard.BrakeHeld);
            _handbrake = touchHandbrake || (keyboardEnabled && _keyboard.HandbrakeHeld);

            if (AwaitingRelease)
            {
                bool anyDown = (touchEnabled && _touch.AnyTouch) || _steer != 0f || _brake || _handbrake;
                if (anyDown)
                {
                    _steer = 0f; _brake = false; _handbrake = false;
                    SteeringFromTouch = false;
                }
                else AwaitingRelease = false;
            }
        }

        /// <summary>
        /// Stop reading input and forget every finger. Used by the rotate gate: a
        /// player who turns the phone mid-corner must not come back to a held steer.
        /// </summary>
        public void Suspend(bool suspended)
        {
            if (Suspended == suspended) return;
            Suspended = suspended;
            AwaitingRelease = !suspended;
            _touch.Clear();
            _steer = 0f;
            _brake = false;
            _handbrake = false;
        }

        /// <summary>Tuning changed at runtime (a test build's debug panel).</summary>
        public void ApplyTuning()
        {
            PushTuning();
        }
    }
}
