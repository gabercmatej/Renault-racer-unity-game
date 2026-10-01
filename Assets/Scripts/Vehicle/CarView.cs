using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.Vehicle
{
    /// <summary>
    /// Draws the car. Presentation only - it reads simulation state and never writes
    /// it, and holds no physics of its own (plan section 4 dependency rule).
    ///
    /// The sprite is positioned by the SAME projection that builds the road, so the car
    /// sits on the surface at any field of view instead of being eyeballed into place.
    ///
    /// PHASE 10: the car is the Renault 5 Turbo 3E drawn from eleven perspective views
    /// (L5..straight..R5), each with a brake-light overlay. The old presentation - one
    /// rear sprite rolled on screen and squashed sideways to fake an angle - is gone: the
    /// car stays upright and planted, and the ANGLE is carried by which view is shown.
    /// <see cref="CarFrameSelector"/> picks the view from the steering the player is
    /// pressing and how long it has been held (quick tap = L1/R1, a held turn walks to
    /// L5/R5, release returns to straight), never from the road's curvature.
    /// </summary>
    public sealed class CarView : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;
        [SerializeField] private SpriteRenderer spriteRenderer;

        [Header("Placement")]
        [Tooltip("How far in front of the camera the car sits, world units. " +
                 "Larger pushes the car further up the screen.")]
        public float distanceAhead = 1700f;

        [Tooltip("Width of the STRAIGHT view as a fraction of the road half width. Every " +
                 "view shares this scale, so angled views are wider exactly as much as " +
                 "their perspective makes them.")]
        [Range(0.1f, 1.2f)] public float widthFraction = 0.70f;

        [Tooltip("Extra vertical nudge in world units, to seat the tyres on the road.")]
        public float verticalOffset = 0f;

        [Header("Directional views")]
        public CarFrameSelector.Settings frames = new CarFrameSelector.Settings();

        [Header("Shadow")]
        [Tooltip("Shadow width relative to the car's FOOTPRINT in the current view (the " +
                 "lowest quarter of its silhouette). Wider than the footprint so the car " +
                 "reads as sitting on the road.")]
        [Range(0.5f, 2f)] public float shadowWidth = 1.5f;

        [Tooltip("Shadow height multiplier over the prepared (very flat) shadow sprite.")]
        [Range(0.3f, 4f)] public float shadowFlatten = 1.7f;

        [Tooltip("How far the shadow follows the footprint toward the side a strong view " +
                 "extends to. 1 = centred under the footprint, 0 = under the rear axle.")]
        [Range(0f, 1f)] public float shadowFollowBody = 1f;

        [Tooltip("Kept below full so the larger shadow grounds the car without becoming a blob.")]
        [Range(0f, 1f)] public float shadowOpacity = 0.85f;

        [Tooltip("Lift of the shadow's centre above the tyre contact line, as a fraction " +
                 "of the car's height. At (or just below) the tyre line, so the shadow shows around " +
                 "the tyres instead of hiding behind the body.")]
        [Range(-0.2f, 0.3f)] public float shadowLift = -0.01f;

        [Header("Rear wheels (effects)")]
        [Tooltip("Rear tyre centres, as a fraction of the straight view's width either " +
                 "side of the rear axle. Smoke and impacts use this; skid marks use the " +
                 "equivalent road-space offset.")]
        [Range(0.1f, 0.6f)] public float rearWheelSpread = 0.36f;

        private CarArt _art;
        private CarFrameSelector _selector;
        private SpriteRenderer _brake;
        private SpriteRenderer _shadow;
        private float _baseScale = 1f;
        private bool _framingClamped;
        private bool _braking;

        // Last placement, for effects. World units.
        private Vector3 _contact;
        private float _worldPerPixel;
        private float _drawnWidth;

        public bool FramingClamped { get { return _framingClamped; } }

        /// <summary>Signed view index currently drawn: 0 straight, -5..-1 left, 1..5 right.</summary>
        public int FrameIndex { get { return _selector != null ? _selector.Index : 0; } }

        /// <summary>Smoothed visual heading in degrees (positive = nose right).</summary>
        public float VisualHeadingDegrees { get { return _selector != null ? _selector.Heading * Mathf.Rad2Deg : 0f; } }

        public bool BrakeLightsOn { get { return _braking; } }
        public bool HasDirectionalArt { get { return _art != null; } }

        /// <summary>Tyre contact point under the rear axle, world units.</summary>
        public Vector3 RearContact { get { return _contact; } }

        /// <summary>
        /// World position of a rear tyre's contact patch in the view currently drawn.
        /// side -1 left, +1 right. Uses the per-view tyre points measured on the art, so
        /// smoke comes off the actual tyres whichever angle is shown.
        /// </summary>
        public Vector3 RearWheel(int side)
        {
            if (_art != null && _worldPerPixel > 0f)
            {
                int slot = CarArt.Slot(FrameIndex);
                Vector2 t = side < 0 ? _art.TyreLeft[slot] : _art.TyreRight[slot];
                return _contact + new Vector3(t.x * _worldPerPixel, t.y * _worldPerPixel, 0f);
            }
            return _contact + new Vector3(side * rearWheelSpread * _drawnWidth, 0f, 0f);
        }

        /// <summary>
        /// A point on one FLANK of the car as it is drawn, for a barrier impact.
        /// side -1 the left flank, +1 the right.
        ///
        /// The burst used to be put at RearContact, which is the car's centre line, so
        /// every scrape sparked out of the middle of the roof no matter which wall had
        /// been hit. The flank is taken from the drawn silhouette's own footprint, so
        /// it follows the body when an angled view swings the car's width across the
        /// contact point rather than sitting at a fixed offset from it, and it is
        /// lifted to about sill height - an impact happens at the bodywork, not at the
        /// road surface and not over the roof.
        /// </summary>
        public Vector3 BodyFlank(int side)
        {
            float halfWidth, centre;
            if (_art != null && _worldPerPixel > 0f)
            {
                int slot = CarArt.Slot(FrameIndex);
                centre = _art.GroundCentre[slot] * _worldPerPixel;
                halfWidth = _art.GroundWidth[slot] * _worldPerPixel * 0.5f;
            }
            else
            {
                centre = 0f;
                halfWidth = _drawnWidth * 0.5f;
            }
            float lift = (_art != null && _worldPerPixel > 0f)
                       ? _art.CarHeightPx * _worldPerPixel * 0.30f
                       : _drawnWidth * 0.20f;
            return _contact + new Vector3(centre + side * halfWidth, lift, 0f);
        }

        /// <summary>
        /// Half the distance between the rear tyres of the STRAIGHT view, in road
        /// half-widths. Skid marks are laid in road space, so this is the offset that
        /// puts each rubber strip under a drawn tyre.
        /// </summary>
        public float RearTrackHalfWidthNormalised
        {
            get
            {
                if (_art == null || _art.StraightWidthPx <= 0f) return rearWheelSpread * widthFraction;
                int s0 = CarArt.Slot(0);
                float halfPx = (_art.TyreRight[s0].x - _art.TyreLeft[s0].x) * 0.5f;
                return halfPx / _art.StraightWidthPx * widthFraction;
            }
        }

        /// <summary>Width of the straight view as drawn this frame, world units.</summary>
        public float DrawnWidth { get { return _drawnWidth; } }

        private void Awake()
        {
            if (spriteRenderer == null) spriteRenderer = GetComponent<SpriteRenderer>();
            _selector = new CarFrameSelector(frames);
            // Guardrails are drawn where the car's BODY meets the barrier: collision is
            // tested on the car's centre, so that is half the drawn width outside the limit.
            if (roadRenderer != null) roadRenderer.railOutset = widthFraction * 0.5f;
            _art = CarArt.Load();

            if (spriteRenderer == null) return;

            if (_art != null)
            {
                spriteRenderer.sprite = _art.Body[CarArt.Slot(0)];
                _brake = MakeChild("BrakeLights", spriteRenderer.sortingOrder + 1);
                _brake.sprite = _art.Brake[CarArt.Slot(0)];
                _brake.enabled = false;
                if (_art.Shadow != null)
                {
                    _shadow = MakeChild("Shadow", spriteRenderer.sortingOrder - 1);
                    _shadow.sprite = _art.Shadow;
                    _shadow.color = new Color(1f, 1f, 1f, shadowOpacity);
                }
                // Scale is set from the straight view's width so every view shares it.
                _worldPerPixel = 1f;
            }
            else if (spriteRenderer.sprite != null)
            {
                // Placeholder fallback: the old single sprite, drawn upright.
                _baseScale = spriteRenderer.sprite.bounds.size.x;
                if (_baseScale <= 0.0001f) _baseScale = 1f;
            }
        }

        private SpriteRenderer MakeChild(string name, int order)
        {
            // Siblings of the body, not children: the body's transform carries the scale
            // and the shadow needs its own.
            GameObject go = new GameObject(name);
            go.transform.SetParent(spriteRenderer.transform.parent, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sharedMaterial = spriteRenderer.sharedMaterial;
            sr.sortingLayerID = spriteRenderer.sortingLayerID;
            sr.sortingOrder = order;
            return sr;
        }

        /// <summary>
        /// Update the car's appearance from simulation state.
        /// </summary>
        /// <param name="carX">Normalised lateral position, 1 = road edge.</param>
        /// <param name="steerInput">Steering the player is pressing, -1..1. Drives the view.</param>
        /// <param name="yaw">Heading relative to the road tangent, radians. + = nose right.</param>
        /// <param name="yawRate">The car's own world rotation rate, rad/s.</param>
        /// <param name="braking">Brake held and controls live - lights the brake lamps.</param>
        /// <param name="dt">Frame delta.</param>
        public void UpdateView(float carX, float steerInput, float yaw, float yawRate, bool braking, float dt)
        {
            if (roadRenderer == null || spriteRenderer == null) return;

            Vector3 pos;
            float roadHalfWorld;
            bool clamped;
            // TryProjectPlayer, not TryProjectRoadPoint: the car is seated on the road
            // under itself and held inside a screen band, so a crest or a descent can
            // never carry it out of view (plan section 10.1).
            if (!roadRenderer.TryProjectPlayer(carX, distanceAhead, out pos,
                                               out roadHalfWorld, out clamped))
                return;
            _framingClamped = clamped;

            float targetWidth = roadHalfWorld * widthFraction;
            _drawnWidth = targetWidth;
            Transform t = spriteRenderer.transform;

            if (_art == null)
            {
                float s = targetWidth / _baseScale;
                t.position = new Vector3(pos.x, pos.y + verticalOffset + targetWidth * 0.30f, 0f);
                t.localRotation = Quaternion.identity;
                t.localScale = new Vector3(s, s, 1f);
                _contact = new Vector3(pos.x, pos.y + verticalOffset, 0f);
                return;
            }

            int index = _selector.Update(steerInput, yaw, yawRate, dt);
            int slot = CarArt.Slot(index);

            // One scale for every view, set by the straight view's width.
            _worldPerPixel = targetWidth / Mathf.Max(1f, _art.StraightWidthPx);
            float scale = _worldPerPixel * CarArt.PixelsPerUnit;

            _contact = new Vector3(pos.x, pos.y + verticalOffset, 0f);

            if (spriteRenderer.sprite != _art.Body[slot]) spriteRenderer.sprite = _art.Body[slot];
            t.position = _contact;
            t.localRotation = Quaternion.identity;
            t.localScale = new Vector3(scale, scale, 1f);

            _braking = braking;
            if (_brake != null)
            {
                if (_brake.sprite != _art.Brake[slot]) _brake.sprite = _art.Brake[slot];
                if (_brake.enabled != braking) _brake.enabled = braking;
                Transform bt = _brake.transform;
                bt.position = _contact;
                bt.localScale = t.localScale;
            }

            if (_shadow != null)
            {
                float centrePx = _art.GroundCentre[slot] * shadowFollowBody;
                float widthWorld = _art.GroundWidth[slot] * _worldPerPixel * shadowWidth;
                float spriteW = _art.Shadow.rect.width;
                float sx = widthWorld / spriteW * CarArt.PixelsPerUnit;
                Transform st = _shadow.transform;
                st.position = new Vector3(_contact.x + centrePx * _worldPerPixel,
                                          _contact.y + _art.CarHeightPx * _worldPerPixel * shadowLift, 0f);
                st.localScale = new Vector3(sx, sx * shadowFlatten, 1f);
            }
        }

        public void ResetView()
        {
            _framingClamped = false;
            _braking = false;
            if (_selector != null) _selector.Reset();
            if (_brake != null) _brake.enabled = false;
        }
    }
}
