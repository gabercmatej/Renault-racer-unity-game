using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// In a pseudo-3D racer the camera is a set of projection parameters, not a
    /// Transform (plan section 10). This owns those parameters and pushes them into
    /// <see cref="RoadRenderer"/> each frame.
    ///
    /// Base framing is live now. The speed/drift/shake responses are wired but held at
    /// zero strength until the Phase 4 feel pass turns them up.
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;

        [Header("Base framing (plan section 10)")]
        [Tooltip("Field of view at rest, degrees. 100 matches the reference.")]
        public float baseFieldOfView = 100f;

        [Tooltip("Camera height above the road, world units.")]
        public float cameraHeight = 1000f;

        [Header("Speed response")]
        [Tooltip("Degrees added to the FOV at full speed. The cheapest 'feels fast' trick.")]
        [Range(0f, 16f)] public float speedFovGain = 8f;

        [Tooltip("How quickly the FOV follows speed, per second.")]
        [Range(0.5f, 8f)] public float fovSmoothing = 2f;

        [Header("Drift and curve response")]
        [Tooltip("Lateral offset toward a slide, in road widths.")]
        [Range(0f, 0.3f)] public float driftPull = 0.12f;

        [Tooltip("Lateral offset into a corner, in road half-widths, at full speed and " +
                 "full curve. RESTRAINED ON PURPOSE (Phase 10d). At 0.10 the camera slid a " +
                 "tenth of a road half-width sideways in every corner without the player " +
                 "touching anything, which reads as the game taking the corner for you: " +
                 "the road swings under the car and the car appears to turn. The lean is " +
                 "still here because a camera that ignores the corner entirely feels " +
                 "stapled to the car, but at 0.035 it is a hint rather than an input. " +
                 "The player's own steering is the thing that should move the picture.")]
        [Range(0f, 0.3f)] public float curveLookAhead = 0.035f;

        [Tooltip("Curve magnitude at which the lean reaches full strength. The old code " +
                 "saturated at curve 6.7 (a hard-coded x0.15), which was fine when the " +
                 "track capped out at 5.5 but means T01, T05 and T14 - 7.5, 8.0 and 7.8 - " +
                 "all now give an IDENTICAL camera lean. Scaling by the hardest corner on " +
                 "the circuit instead keeps the corners distinguishable.")]
        [Range(2f, 12f)] public float curveLookAheadFullAt = 8f;

        [Tooltip("How quickly the lateral offset eases, per second.")]
        [Range(1f, 16f)] public float lateralSmoothing = 4f;

        [Header("Impact shake")]
        [Tooltip("Shake decay time, seconds.")]
        public float shakeDuration = 0.25f;

        [Tooltip("Peak shake offset in road widths at a full-speed impact.")]
        [Range(0f, 0.3f)] public float shakeAmplitude = 0.08f;

        private float _fov;
        private float _lateralOffset;
        private float _shakeTime;
        private float _shakeStrength;

        /// <summary>Current projected field of view, for HUD/debug.</summary>
        public float CurrentFieldOfView { get { return _fov; } }

        private void Awake()
        {
            _fov = baseFieldOfView;
            if (roadRenderer != null) roadRenderer.cameraHeight = cameraHeight;
        }

        /// <summary>
        /// Drive the camera from the car's state. Called once per rendered frame after
        /// the simulation has stepped.
        /// </summary>
        /// <param name="carZ">Distance along the track.</param>
        /// <param name="carX">Normalised lateral position.</param>
        /// <param name="speedPct">0..1 fraction of top speed.</param>
        /// <param name="lateralVel">Signed lateral velocity, for the drift pull.</param>
        /// <param name="curve">Curve strength of the segment under the car.</param>
        /// <param name="dt">Unscaled frame delta.</param>
        public void Follow(float carZ, float carX, float speedPct,
                           float lateralVel, float curve, float dt)
        {
            if (roadRenderer == null) return;

            // FOV stretches with speed, eased so it never snaps.
            float targetFov = baseFieldOfView + speedFovGain * Mathf.Clamp01(speedPct);
            _fov = Mathf.Lerp(_fov, targetFov, 1f - Mathf.Exp(-fovSmoothing * dt));

            // Lateral: lean toward the slide and into the corner, both capped.
            //
            // The two terms are NOT the same kind of thing and only one of them was ever
            // in question. driftPull follows lateralVel, which is a consequence of what
            // the player did - the camera leaning into a slide the player provoked reads
            // as the camera reacting to them. curveLookAhead follows the ROAD, so it moves
            // the picture whether or not the player did anything, and at its old strength
            // that was enough to make a corner look self-driven. See the tooltips.
            float lean = Mathf.Clamp(curve / Mathf.Max(0.01f, curveLookAheadFullAt), -1f, 1f);
            float targetLateral = Mathf.Clamp(lateralVel, -1f, 1f) * driftPull
                                + lean * curveLookAhead * speedPct;
            _lateralOffset = Mathf.Lerp(_lateralOffset, targetLateral,
                                        1f - Mathf.Exp(-lateralSmoothing * dt));

            // Decaying positional shake. Never rotational - that hurts readability.
            float shake = 0f;
            if (_shakeTime > 0f)
            {
                _shakeTime -= dt;
                float k = Mathf.Clamp01(_shakeTime / Mathf.Max(0.0001f, shakeDuration));
                shake = Mathf.Sin(Time.time * 90f) * shakeAmplitude * _shakeStrength * k;
            }

            roadRenderer.fieldOfView = _fov;
            roadRenderer.cameraHeight = cameraHeight;
            roadRenderer.CameraZ = carZ;
            roadRenderer.CameraX = carX + _lateralOffset + shake;
        }

        /// <summary>Trigger an impact shake. <paramref name="strength"/> is 0..1.</summary>
        public void Impact(float strength)
        {
            _shakeStrength = Mathf.Clamp01(strength);
            _shakeTime = shakeDuration;
        }

        /// <summary>Reset to rest, used on race restart.</summary>
        public void ResetRig()
        {
            _fov = baseFieldOfView;
            _lateralOffset = 0f;
            _shakeTime = 0f;
            _shakeStrength = 0f;
        }
    }
}
