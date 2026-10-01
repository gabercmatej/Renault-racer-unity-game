using UnityEngine;
using RenaultRacer.Web;

namespace RenaultRacer.InputSystem
{
    /// <summary>
    /// Touch input (plan section 9, mobile is the primary control scheme).
    ///
    /// The scheme is the reference game's, because it is proven with exactly this
    /// audience: two full-height invisible gutters, left half steers left, right half
    /// steers right, plus one visible brake button. Nothing to learn, nothing to aim
    /// at, and it works with thumbs wherever they happen to rest on a landscape phone.
    ///
    /// Three things make it survive real hands:
    ///
    /// 1. **Every finger is tracked by id, and keeps the role it was pressed with.**
    ///    A finger that lands on the brake stays a brake finger until it lifts, even
    ///    if the thumb rolls off the button; a finger that lands in a gutter keeps
    ///    steering even if it drifts across the screen centre. The reference had to
    ///    hack around a brake press cancelling steering - this is the actual fix, and
    ///    it is also why two fingers can steer and brake at once.
    /// 2. **Output is raw.** Like the keyboard, this emits -1/0/+1 and nothing else.
    ///    All smoothing happens once, in CarSimulation, so a thumb and a key produce
    ///    identical handling - which is the whole point of the IDriveInput split.
    /// 3. **Zero allocation.** Legacy Input with GetTouch(i) rather than Input.touches
    ///    (which allocates an array every call), and a fixed finger table.
    ///
    /// Placeholder geometry only. Phase 7/10 restyle the visuals; the zones live here.
    /// </summary>
    public sealed class TouchDriveInput : IDriveInput
    {
        /// <summary>What a finger is doing. Assigned on touch-down, kept until release.</summary>
        private enum Role : byte { None = 0, SteerLeft, SteerRight, Brake }

        private const int MaxFingers = 10;

        private readonly int[] _fingerId = new int[MaxFingers];
        private readonly Role[] _fingerRole = new Role[MaxFingers];
        private int _fingerCount;

        // --- tuning, all normalised so one set of numbers fits every phone ---------

        /// <summary>Where the steering gutters meet, as a fraction of screen width.</summary>
        public float steerSplit = 0.5f;

        /// <summary>Brake button size as a fraction of the SHORTER screen edge.</summary>
        public float brakeSize = 0.30f;

        /// <summary>Brake button margin from the safe-area corner, same units.</summary>
        public float brakeMargin = 0.05f;

        /// <summary>
        /// Which bottom corner the brake button sits in. Left is the V1 choice, taken
        /// from the M6 device test - the player wanted the left hand on the brake.
        ///
        /// Whichever side it is on, the button overlaps that steering gutter and wins
        /// any touch that starts inside it, so BRAKING AND STEERING TOWARD THE BUTTON'S
        /// OWN SIDE NEEDS A SECOND FINGER. Putting it left moves that cost from
        /// steering right to steering left; it does not remove it. See plan 9.1.
        /// </summary>
        public bool brakeOnLeft = true;

        /// <summary>
        /// How long brake and steer must be held together before it counts as a
        /// deliberate drift request. Without this every trail-braked corner would
        /// provoke a slide, which is not what a brake dab means.
        /// </summary>
        public float handbrakeHoldSeconds = 0.18f;

        /// <summary>
        /// Treat the mouse as a single finger. Lets the layout be driven in the Editor
        /// and in a desktop browser, where there is no touch at all.
        /// </summary>
        public bool emulateWithMouse = true;

        // --- state ----------------------------------------------------------------

        private float _steer;
        private bool _brake;
        private bool _handbrake;
        private float _brakeAndSteerHeld;

        private Rect _brakeRect;
        private int _cachedWidth = -1, _cachedHeight = -1;
        private Rect _cachedSafeArea;

        /// <summary>True while at least one finger is on the screen. Drives the hint fade.</summary>
        public bool AnyTouch { get; private set; }

        /// <summary>Brake button rect in SCREEN pixels, for the placeholder view to draw.</summary>
        public Rect BrakeRect { get { return _brakeRect; } }

        /// <summary>Where the steering gutters meet, in screen pixels.</summary>
        public float SplitX { get { return Screen.width * steerSplit; } }

        public float SteerAxis { get { return _steer; } }
        public bool BrakeHeld { get { return _brake; } }
        public bool HandbrakeHeld { get { return _handbrake; } }

        /// <summary>Call once per frame, before anything reads the properties.</summary>
        public void Poll(float unscaledDeltaTime)
        {
            RecomputeLayoutIfNeeded();

            bool left = false, right = false, brake = false;
            int liveCount = 0;

            int touches = UnityEngine.Input.touchCount;
            for (int i = 0; i < touches; i++)
            {
                Touch t = UnityEngine.Input.GetTouch(i);

                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    Release(t.fingerId);
                    continue;
                }

                liveCount++;
                Role role = t.phase == TouchPhase.Began
                    ? Assign(t.fingerId, Classify(t.position))
                    : Lookup(t.fingerId, t.position);

                Apply(role, ref left, ref right, ref brake);
            }

            // Mouse stands in for a single finger where there is no touch screen.
            if (emulateWithMouse && touches == 0)
            {
                if (UnityEngine.Input.GetMouseButton(0))
                {
                    liveCount++;
                    Vector2 p = UnityEngine.Input.mousePosition;
                    Role role = UnityEngine.Input.GetMouseButtonDown(0)
                        ? Assign(MouseFingerId, Classify(p))
                        : Lookup(MouseFingerId, p);
                    Apply(role, ref left, ref right, ref brake);
                }
                else Release(MouseFingerId);
            }

            AnyTouch = liveCount > 0;

            // Both gutters pressed cancels out, exactly like both arrow keys.
            _steer = (left == right) ? 0f : (right ? 1f : -1f);
            _brake = brake;

            TickHandbrake(_brake, _steer, unscaledDeltaTime);
        }

        /// <summary>
        /// The drift gesture: brake and steer held TOGETHER for
        /// <see cref="handbrakeHoldSeconds"/>. No third button (plan section 9), and a
        /// hold rather than a press because a brake dab mid-corner is a brake dab, not
        /// a request to break the rear loose.
        ///
        /// Split out of <see cref="Poll"/> so the gesture can be verified directly
        /// (HandlingProbe [14]) instead of being taken on trust - the rest of Poll
        /// reads real hardware, which a headless test cannot supply.
        /// </summary>
        public void TickHandbrake(bool brake, float steer, float dt)
        {
            if (brake && steer != 0f) _brakeAndSteerHeld += dt;
            else _brakeAndSteerHeld = 0f;
            _handbrake = _brakeAndSteerHeld >= handbrakeHoldSeconds;
        }

        /// <summary>Seconds brake+steer have been held together. Debug/verification.</summary>
        public float HandbrakeHoldProgress { get { return _brakeAndSteerHeld; } }

        /// <summary>Drop every finger - used when control is taken away (rotate gate).</summary>
        public void Clear()
        {
            _fingerCount = 0;
            _steer = 0f;
            _brake = false;
            _handbrake = false;
            _brakeAndSteerHeld = 0f;
            AnyTouch = false;
        }

        // --- internals -------------------------------------------------------------

        // Outside any real fingerId range, so the mouse cannot collide with a finger.
        private const int MouseFingerId = int.MinValue;

        private static void Apply(Role role, ref bool left, ref bool right, ref bool brake)
        {
            if (role == Role.SteerLeft) left = true;
            else if (role == Role.SteerRight) right = true;
            else if (role == Role.Brake) brake = true;
        }

        private Role Classify(Vector2 screenPos)
        {
            // The brake wins the overlap. It sits in a bottom corner, which is also
            // inside a steering gutter, and a brake press must never be reinterpreted.
            // Ownership is by pointer id from here on (see Assign/Lookup), so a thumb
            // that rolls off the button keeps braking and a second finger can steer
            // at the same time.
            if (_brakeRect.Contains(screenPos)) return Role.Brake;
            return screenPos.x < SplitX ? Role.SteerLeft : Role.SteerRight;
        }

        private Role Assign(int fingerId, Role role)
        {
            for (int i = 0; i < _fingerCount; i++)
            {
                if (_fingerId[i] == fingerId) { _fingerRole[i] = role; return role; }
            }
            if (_fingerCount < MaxFingers)
            {
                _fingerId[_fingerCount] = fingerId;
                _fingerRole[_fingerCount] = role;
                _fingerCount++;
            }
            return role;
        }

        /// <summary>
        /// The role this finger was pressed with. A finger whose down-event we never
        /// saw - possible if a touch begins while the gate is up, or on the first frame
        /// after a focus change - is classified from where it is now rather than
        /// dropped, so control is never silently dead.
        /// </summary>
        private Role Lookup(int fingerId, Vector2 screenPos)
        {
            for (int i = 0; i < _fingerCount; i++)
                if (_fingerId[i] == fingerId) return _fingerRole[i];
            return Assign(fingerId, Classify(screenPos));
        }

        private void Release(int fingerId)
        {
            for (int i = 0; i < _fingerCount; i++)
            {
                if (_fingerId[i] != fingerId) continue;
                _fingerCount--;
                _fingerId[i] = _fingerId[_fingerCount];
                _fingerRole[i] = _fingerRole[_fingerCount];
                return;
            }
        }

        /// <summary>
        /// Rebuild the brake rect only when the screen actually changes. A browser
        /// resize, an orientation change and the URL bar sliding away all land here.
        /// </summary>
        private void RecomputeLayoutIfNeeded()
        {
            // WebSafeArea, not Screen.safeArea - the latter reports the whole canvas
            // on WebGL, which would put the brake button under a notch or the home
            // indicator depending on which way the phone was turned.
            Rect safe = WebSafeArea.Get();
            if (Screen.width == _cachedWidth && Screen.height == _cachedHeight
                && safe == _cachedSafeArea)
                return;

            _cachedWidth = Screen.width;
            _cachedHeight = Screen.height;
            _cachedSafeArea = safe;

            float shortEdge = Mathf.Min(Screen.width, Screen.height);
            float size = shortEdge * brakeSize;
            float margin = shortEdge * brakeMargin;

            // Anchored inside the SAFE area, so the home indicator and a rounded
            // corner cannot eat the one button the player has to hit.
            float x = brakeOnLeft ? safe.xMin + margin
                                  : safe.xMax - margin - size;
            float y = safe.yMin + margin;
            _brakeRect = new Rect(x, y, size, size);
        }
    }
}
