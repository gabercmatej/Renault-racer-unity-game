using UnityEngine;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// Lightning boost pickups: the scene half. The rule (where they are, which are taken)
    /// is BoostPickupSet, pure; the boost itself is CarSimulation.TriggerBoost, stepped on
    /// the fixed clock. This component only DRAWS them and hands the sweep through.
    ///
    /// WHY NOT A ROADSIDE SPRITE KIND. RoadsideSprites is the obvious host and it is the
    /// wrong one: its visible cap is a per-tier budget (24 on Weak, two thirds reserved for
    /// signs), so a pickup - which is GAMEPLAY - could be culled on exactly the device least
    /// able to afford a surprise; it disables itself outright when its atlas is missing or
    /// spawnProps is off; and its slots are one-per-side-per-segment measured from the
    /// barrier. What IS reused is its projection: the same RoadRenderer.SegmentSample
    /// (worldX/halfWidth/worldY/clipY/fog), the same PropSprite material, the same depth
    /// sorting - so a pickup is attached to the road by the one path every prop uses, and
    /// any change to that projection (e.g. a camera-yaw shift) moves it with the road.
    ///
    /// The GameObject must sit at the origin with unit scale: children are placed in local
    /// space, exactly like RoadsidePool.
    ///
    /// Zero allocation per frame: one SpriteRenderer per pickup, built once, plus ONE
    /// pooled renderer for the collection burst, restarted (never stacked) on a repeat.
    ///
    /// THE COLLECTION BURST. Taking a pickup hides it at once and flashes the electric-arc
    /// overlay (PickupArt.Arcs) around the car for burstSeconds - off the same one-shot
    /// event that hands the boost over, never proximity. It is placed on the DRAWN car
    /// every frame (CarView: rear contact, flanks, straight-view width), so it follows the
    /// car and its turn frames; its empty middle keeps the car readable. It replaces the
    /// old grow-and-fade pop of the bolt and the cyan spark burst, which together read as
    /// a generic sparkle. AudioDirector plays the zap off the same event (the count
    /// CarController raises on the taking step).
    /// </summary>
    public sealed class BoostPickups : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;

        [Tooltip("The drawn car, for the collection burst. Found at startup if empty.")]
        [SerializeField] private CarView carView;

        public BoostTuning tuning = new BoostTuning();

        // WHY THE FIRST VERSION COULD NOT BE SEEN. It was sized and sorted like scenery:
        // a 1.1 m bolt at the props' sorting orders (10-90), hazed by distance fog. By
        // perspective alone that is ~12 px tall a second ahead of the car, washed toward
        // the haze colour - and by the time it is big enough to read it is at the car's
        // own depth, where the car (sorting 100) is drawn over it. So a player saw nothing
        // until the pickup triggered. It is a GAMEPLAY object and is drawn as one: larger,
        // never smaller than minScreenHeightPx, unhazed, above every prop and the tyre
        // wall, from far enough ahead to change line for it. PickupVisibilityProbe gates it.

        // SIZE, 2026-10. The placeholder at 3.2 m drew the bolt far taller than the car at
        // the car, and its body was wider than the collection radius: it looked hit when
        // it was missed. 2.0 m puts the bolt's body at ~0.25 road half-widths wide, so
        // car (0.35 each side) + bolt (~0.13 each side) = 0.48 against radius 0.45 - what
        // looks like contact IS contact. The far-away floor drops 64 -> 52 px (the body
        // ~42 px): still the brightest thing on the road from drawDistance, not a sign.
        // (Now 1.64 m, below: body half-width 0.109 at the car, so contact at 0.46.)

        [Header("Look")]
        [Tooltip("Height of the sprite (bolt + baked glow), metres before artScale (one " +
                 "segment = one metre). The bolt's body is PickupArt.BoltBodyHeight of it.")]
        [Range(0.3f, 6f)] public float heightMetres = 1.55f;

        // DISTANCE SCALE, 2026-10-07. The 52 px floor above was FLAT: true perspective
        // makes the bolt a few px tall at drawDistance, so the floor won for nearly the
        // whole approach - the pickup appeared 2.6 s out at 52 px and stayed there until
        // the last moment, then grew to ~90 at the car. Huge far away, same size at the
        // pickup: it read as a HUD icon, not an object on the road. The floor now EASES
        // with depth - nearSizePx * (farScale + (1-farScale) * (1-t)^scaleExponent),
        // t = 0 at the car, 1 at drawDistance - and the drawn size is the larger of that
        // and true perspective. Both grow monotonically as the car closes, so the size
        // does too, with no step; near the car perspective wins and the size Maj approved
        // there is untouched. PickupVisibilityProbe [scale] gates the curve.
        // SECOND PASS, 2026-10-07. 22/33/47/66/90 px (first seen / 1.5 / 1.0 / 0.5 s /
        // at the car) still read too big, most of all far away. Maj's targets: ~14-18,
        // 22-27, 30-38, 45-55, 70-78. The bolt is 2.0 -> 1.64 m (perspective at the car
        // 90 -> 74 px; it sets the near size) and the floor 88 px / 25% / ^1.6 -> 74 px /
        // 22% / ^1.9: ~16 / 23 / 34 / 52 / 74 (PickupVisibilityProbe [scale]).
        // THIRD PASS, 2026-10-07. Still too big at the far end. Bolt 1.64 -> 1.55 m (near
        // 74 -> 70 px), floor 74 px / 22% / ^1.9 -> 70 px / 14% / ^1.7: first seen ~16 -> ~10
        // px (60%), 1.5 s ~23 -> ~19, 1.0 s ~34 -> ~31, 0.5 s ~52 -> ~49. 10 px on the 720-px
        // screen is 15 px at 1080p: well over the ~6-8 px a bolt needs to stay a distinct
        // bright shape against the road (the glow is baked into the sprite, unhazed).
        [Tooltip("Size floor at the car, px on the 720-px virtual screen. Perspective alone " +
                 "measures ~70 there at heightMetres 1.55, and wins near the car.")]
        [Range(0f, 160f)] public float nearSizePx = 70f;

        [Tooltip("Floor at drawDistance as a fraction of nearSizePx (~10 px).")]
        [Range(0.05f, 1f)] public float farScale = 0.14f;

        [Tooltip("Easing of the floor between the car and drawDistance. 1 = linear; higher " +
                 "keeps it small for longer and grows it late.")]
        [Range(0.5f, 4f)] public float scaleExponent = 1.7f;

        [Tooltip("Depth of the drawn car, segments (CarView.distanceAhead / SegmentLength): " +
                 "where the floor reaches nearSizePx.")]
        [Range(0f, 30f)] public float carDepthSegments = 8.5f;

        [Tooltip("Sorting order: above props (10-90) and the tyre wall (95), below the car (100).")]
        public int sortingOrder = 97;

        [Tooltip("Same meaning as RoadsideSprites.artScale, so a pickup is in proportion with the signs.")]
        [Range(0.5f, 4f)] public float artScale = 2.6f;

        [Tooltip("Hover above the road, metres before artScale. It bobs around this.")]
        [Range(0f, 1f)] public float hoverMetres = 0.15f;

        // APPROACH LIFT, 2026-10. On a small landscape phone the bolt a second or two out sits
        // on the road row just above the car's roofline, and for the last stretch the car
        // (sorting 100) covers it. The bolt is raised by a FRACTION OF ITS OWN BODY HEIGHT -
        // so it scales with the size curve above and can never read as a balloon or a HUD
        // overlay - and the raise eases (smoothstep, no pop) to exactly zero at the
        // collection distance, where the drawn position IS the collision position. Size,
        // lane, sorting, fog and the sweep are untouched: it is purely a vertical offset.
        [Header("Approach lift")]
        [Tooltip("Raise above the road while approaching, as a multiple of the bolt body's " +
                 "drawn height. 0 = off (the old behaviour).")]
        [Range(0f, 2f)] public float approachLift = 1.0f;

        // LANDING BY TIME-TO-CONTACT, 2026-10. The lift is judged by how long until the car
        // reaches the bolt (distance to the drawn car / the car's current speed), not by
        // distance: at a third of top speed a distance-based landing left the bolt in the air
        // until the car was almost on it. It is exactly 0 from approachLandSeconds out, full
        // from approachFullSeconds out, a smoothstep between. A distance floor keeps a stopped
        // or crawling car from ever having a hovering bolt in front of it.
        [Tooltip("Time-to-contact (s) at and below which the bolt is fully landed (lift 0).")]
        [Range(0.3f, 3f)] public float approachLandSeconds = 1.0f;

        [Tooltip("Time-to-contact (s) at and above which the lift is full.")]
        [Range(0.5f, 5f)] public float approachFullSeconds = 2.1f;

        [Tooltip("Zero lift within this many segments of the drawn car, whatever the speed " +
                 "(a stopped car).")]
        [Range(0f, 40f)] public float approachFloorSegs = 10f;

        [Tooltip("Segments beyond the floor over which the lift eases in (distance side).")]
        [Range(1f, 40f)] public float approachRampSegs = 8f;

        [Tooltip("Clearance part of the lift: the body's foot is brought up to the car's roofline " +
                 "minus this fraction of the body's height (0 = foot at the roof).")]
        [Range(0f, 1f)] public float approachClearBody = 0.15f;

        [Tooltip("Cap on the clearance lift, as a fraction of the drawn car's height.")]
        [Range(0f, 1.5f)] public float approachMaxCarFraction = 0.6f;

        [Tooltip("Segments ahead beyond which a pickup is not drawn. 140 is about 2.2 s at " +
                 "top speed to the drawn car (was 160, 2.6 s: long enough to read as a landmark).")]
        [Range(20, 300)] public int drawDistance = 140;

        [Tooltip("Segments over which it fades up as it comes inside drawDistance.")]
        [Range(1, 80)] public int fadeSegments = 12;

        [Tooltip("Seconds per float cycle (the pack asks 1.6-2.0). Each pickup has its own phase.")]
        [Range(0.5f, 4f)] public float bobPeriod = 1.8f;

        [Tooltip("Float amplitude, fraction of the bolt's visible height (the pack asks 3-5%).")]
        [Range(0f, 0.2f)] public float bobAmplitude = 0.04f;

        [Tooltip("Scale pulse on the same cycle, fraction. Opacity never pulses: readability.")]
        [Range(0f, 0.1f)] public float pulseAmount = 0.02f;

        [Header("Collection burst")]
        [Tooltip("Seconds the electric arcs show around the car after a pickup is taken.")]
        [Range(0.1f, 1f)] public float burstSeconds = 0.25f;

        [Tooltip("Overlay width, multiple of the car's drawn width (the pack starts at 1.3).")]
        [Range(0.8f, 2.5f)] public float burstWidthOfCar = 1.3f;

        [Tooltip("Peak opacity. A cap: a chain of pickups restarts the burst and never " +
                 "brightens it.")]
        [Range(0.1f, 1f)] public float burstPeakAlpha = 1f;

        [Tooltip("Sorting order: over the car (100) and its brake lights (101), under the " +
                 "impact sparks (150). The HUD is a screen overlay above all of it.")]
        public int burstSortingOrder = 120;

        private BoostPickupSet _set;
        private short[] _atSegment;          // pickup index per track segment, -1 = none
        private SpriteRenderer[] _renderers;
        private Transform[] _transforms;
        private bool[] _drawn;
        private Sprite _sprite;
        private SpriteRenderer _burst;
        private Transform _burstT;
        private float _burstAge = -1f;       // seconds since the last take, < 0 = idle
        private float _camFrac;             // camera's fractional position within its segment
        private float _topY;                 // world y of the highest drawn road row this frame

        /// <summary>The car's forward speed, units/s, as of the last sweep. Drives the landing timing. Settable for probes.</summary>
        public float CarSpeed { get; set; }

        public BoostPickupSet Set { get { return _set; } }

        /// <summary>The renderer for pickup <paramref name="i"/>, for probes.</summary>
        public SpriteRenderer RendererOf(int i) { return _renderers[i]; }

        /// <summary>Wire up and build without a scene (probes). Play mode uses Awake/Start.</summary>
        public void Initialise(RoadRenderer road, TrackModel track, CarView car = null)
        {
            roadRenderer = road;
            carView = car;
            _sprite = PickupArt.Lightning;
            Build(track);
        }

        /// <summary>For RaceValidator: the most lap time all pickups together can save.</summary>
        public float MaxSecondsSaved { get { return _set != null ? tuning.MaxSecondsSaved(_set.Count) : 0f; } }

        private void Awake()
        {
            if (roadRenderer == null) roadRenderer = FindAnyObjectByType<RoadRenderer>();
            if (carView == null) carView = FindAnyObjectByType<CarView>();
            _sprite = PickupArt.Lightning;
        }

        private void Start()
        {
            // The track is built in TrackRuntime.Awake, so the set is made here - the same
            // moment RoadsideSprites builds its layout.
            if (roadRenderer == null || roadRenderer.Track == null) { enabled = false; return; }
            Build(roadRenderer.Track);
        }

        public void Build(TrackModel t)
        {
            _set = BoostPickupSet.FromLayout(t, tuning);
            _atSegment = new short[t.Count];
            for (int i = 0; i < _atSegment.Length; i++) _atSegment[i] = -1;
            for (int i = 0; i < _set.Count; i++) _atSegment[_set.SegmentOf(i)] = (short)i;

            Material mat = Resources.Load<Material>(RoadsideSprites.MaterialPath);
            int n = _set.Count;
            _renderers = new SpriteRenderer[n];
            _transforms = new Transform[n];
            _drawn = new bool[n];
            for (int i = 0; i < n; i++)
            {
                GameObject go = new GameObject("Pickup" + i);
                go.transform.SetParent(transform, false);
                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = _sprite;
                if (mat != null) sr.sharedMaterial = mat;
                sr.enabled = false;
                _renderers[i] = sr;
                _transforms[i] = go.transform;
            }
            BuildBurst();
            Debug.Log("[BoostPickups] " + n + " pickups"
                      + (PickupArt.IsPlaceholder ? " (placeholder art)" : ""));
        }

        /// <summary>
        /// Called by CarController once per SIMULATION step with where the car is DRAWN
        /// (z + distanceAhead). True if a pickup was taken - and then the boost has already
        /// been handed to the simulation.
        /// </summary>
        public bool Sweep(CarSimulation sim, float zFrom, float zTo, float xFrom, float xTo)
        {
            if (sim != null) CarSpeed = Mathf.Abs(sim.Speed);
            if (_set == null || sim == null) return false;
            _set.Radius = tuning.radius;   // live-tunable in the Inspector
            int i = _set.Sweep(zFrom, zTo, xFrom, xTo);
            if (i < 0) return false;
            sim.TriggerBoost(tuning.addedTopSpeed, tuning.addedAccel, tuning.holdSeconds, tuning.fadeSeconds);
            _burstAge = 0f;   // restart, never a second overlay
            return true;
        }

        /// <summary>Every pickup back, burst off. Race restart (CarController.ResetCar).</summary>
        public void ResetAll()
        {
            if (_set != null) _set.ResetAll();
            _burstAge = -1f;
            if (_burst != null) _burst.enabled = false;
        }

        /// <summary>True while the collection burst is showing. For probes.</summary>
        public bool BurstActive { get { return _burst != null && _burst.enabled; } }

        /// <summary>The burst's renderer, or null without arc art. For probes.</summary>
        public SpriteRenderer BurstRenderer { get { return _burst; } }

        /// <summary>Start the burst as a take would. For probes; play mode uses Sweep.</summary>
        public void StartBurstForProbe() { _burstAge = 0f; }

        /// <summary>
        /// Set by CarController.Paused. While true the bob and the collection burst stand
        /// still: time that passes frozen is subtracted from the bob clock, so resuming
        /// continues from the same pose instead of jumping.
        /// </summary>
        public bool Frozen { get; set; }
        private float _frozenTotal;

        private void LateUpdate()
        {
            if (Frozen) _frozenTotal += Time.deltaTime;
            Refresh();
            UpdateBurst(Frozen ? 0f : Time.deltaTime);
        }

        private void BuildBurst()
        {
            if (_burst != null) return;
            Sprite arcs = PickupArt.Arcs;
            if (arcs == null) return;
            GameObject go = new GameObject("PickupBurst");
            go.transform.SetParent(transform, false);
            _burst = go.AddComponent<SpriteRenderer>();   // the default sprite material, shared
            _burst.sprite = arcs;
            _burst.sortingOrder = burstSortingOrder;
            _burst.enabled = false;
            _burstT = go.transform;
        }

        /// <summary>
        /// The burst's opacity at <paramref name="age"/> seconds: 0.02 s up to the peak,
        /// held to a quarter of the life, then a smoothstep down to exactly zero at the end.
        /// </summary>
        public static float BurstAlpha(float age, float life)
        {
            if (age < 0f || age >= life) return 0f;
            const float rise = 0.02f;
            if (age < rise) return age / rise;
            float hold = life * 0.25f;
            if (age <= hold) return 1f;
            float t = (age - hold) / Mathf.Max(0.0001f, life - hold);
            return 1f - t * t * (3f - 2f * t);
        }

        /// <summary>Advance and place the collection burst. Public so a probe can drive it.</summary>
        public void UpdateBurst(float dt)
        {
            if (_burst == null) return;
            if (_burstAge < 0f || carView == null || carView.DrawnWidth <= 0f)
            {
                if (_burst.enabled) _burst.enabled = false;
                return;
            }
            _burstAge += dt;
            float a = BurstAlpha(_burstAge, burstSeconds);
            if (a <= 0f)
            {
                _burstAge = -1f;
                if (_burst.enabled) _burst.enabled = false;
                return;
            }

            // On the drawn car: centred between its flanks (so it follows an angled turn
            // frame's body), seated on its rear contact line, sized by the straight
            // view's width so it does not breathe as the frames change.
            Vector3 contact = carView.RearContact;
            float centreX = 0.5f * (carView.BodyFlank(-1).x + carView.BodyFlank(1).x);
            float life01 = Mathf.Clamp01(_burstAge / burstSeconds);
            float spriteW = _burst.sprite.rect.width / _burst.sprite.pixelsPerUnit;
            float k = carView.DrawnWidth * burstWidthOfCar / spriteW * Mathf.Lerp(0.96f, 1.06f, life01);
            _burstT.position = new Vector3(centreX, contact.y, 0f);
            _burstT.localScale = new Vector3(k, k, 1f);
            _burst.color = new Color(1f, 1f, 1f, a * burstPeakAlpha);
            if (!_burst.enabled) _burst.enabled = true;
        }

        /// <summary>Place every pickup for the current frame. Public so a probe can drive it.</summary>
        public void Refresh()
        {
            if (_set == null || roadRenderer == null || _sprite == null) return;

            for (int i = 0; i < _drawn.Length; i++) _drawn[i] = false;

            float length = roadRenderer.Track.Length;
            float camSeg = Mathf.Floor(roadRenderer.CameraZ / RoadGeometry.SegmentLength);
            _camFrac = roadRenderer.CameraZ / RoadGeometry.SegmentLength - camSeg;   // s.depth counts whole segments from camSeg; the camera is part-way into it
            // The lap the DRAWN car is on. The road draws straight over the line, so on the
            // run to the flag its samples already hold the start of the next lap - and a
            // pickup riding them showed lap 2's first bolt ahead in a one-lap race. Only
            // copies on the car's own lap (or behind it) are drawn, and none on a lap the
            // race does not have (BoostPickupSet.IsLive).
            float carAhead = carView != null ? carView.distanceAhead : carDepthSegments * RoadGeometry.SegmentLength;
            // Never below lap 0: the race starts there, and a camera before the line (only a
            // probe puts one there) is still on the way to lap 0's pickups.
            int carLap = Mathf.Max(0, Mathf.FloorToInt((roadRenderer.CameraZ + carAhead) / length));
            float anisotropy = 1f / Mathf.Max(0.1f, roadRenderer.referenceAspect);
            Camera cam = Camera.main;
            float viewHalfWidth = cam != null ? cam.orthographicSize * cam.aspect : 10f;
            float ppu = roadRenderer.virtualHeight / (2f * (cam != null ? cam.orthographicSize : 5f));
            float cycle = (Time.time - _frozenTotal) * (2f * Mathf.PI / Mathf.Max(0.1f, bobPeriod));

            int samples = roadRenderer.SampleCount;
            // The approach lift never carries a bolt above the far end of the visible road:
            // beyond it is sky or a crest, and a bolt there would read as hanging in the air.
            _topY = float.NegativeInfinity;
            for (int k = 0; k < samples; k++)
            {
                RoadRenderer.SegmentSample t = roadRenderer.GetSample(k);
                if (!t.hidden && t.worldY > _topY) _topY = t.worldY;
            }
            for (int k = 0; k < samples; k++)
            {
                RoadRenderer.SegmentSample s = roadRenderer.GetSample(k);
                if (s.depth > drawDistance) break;                 // samples run nearest first
                if (s.hidden) continue;                            // behind a crest
                if (s.index < 0 || s.index >= _atSegment.Length) continue;
                int i = _atSegment[s.index];
                if (i < 0) continue;

                // Which lap this copy of the segment belongs to, from depth (not from the
                // visible-list index - see RoadRenderer's note on `depth`).
                int lap = Mathf.FloorToInt((camSeg + s.depth) * RoadGeometry.SegmentLength / length);
                if (lap > carLap || !_set.IsLive(i, lap)) continue;  // next lap, past the flag, or taken
                // A stable phase per pickup (golden angle apart), so they never float in step.
                float phase = cycle + i * 2.39996f;
                if (Place(i, s, Mathf.Sin(phase), Mathf.Cos(phase), anisotropy, viewHalfWidth, ppu)) _drawn[i] = true;
            }

            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i].enabled != _drawn[i]) _renderers[i].enabled = _drawn[i];
        }

        /// <summary>
        /// The size floor at <paramref name="depth"/> segments ahead, px on the 720-px
        /// virtual screen: nearSizePx at the car, easing down to farScale of it at
        /// drawDistance. Monotonic in depth, so the drawn size only ever grows on approach.
        /// </summary>
        public float FloorPx(float depth)
        {
            float span = Mathf.Max(1f, drawDistance - carDepthSegments);
            float t = Mathf.Clamp01((depth - carDepthSegments) / span);
            return nearSizePx * (farScale + (1f - farScale) * Mathf.Pow(1f - t, scaleExponent));
        }

        /// <summary>
        /// 0..1 weight of the approach lift for a bolt <paramref name="depth"/> segments ahead of
        /// the camera with the car at <paramref name="speed"/> units/s: 0 within the distance
        /// floor of the drawn car and at time-to-contact <= approachLandSeconds, 1 at
        /// time-to-contact >= approachFullSeconds, smoothsteps between. Non-increasing as the
        /// car closes at a steady speed. Speed ~0 counts as far in time (the floor still lands it).
        /// </summary>
        public float ApproachEase(float depth, float speed, float carSegs)
        {
            float dSegs = depth - carSegs;
            float dist = Mathf.Clamp01((dSegs - approachFloorSegs) / Mathf.Max(0.01f, approachRampSegs));
            float ttc = speed > 1f ? dSegs * RoadGeometry.SegmentLength / speed : 1e6f;
            float span = Mathf.Max(0.05f, approachFullSeconds - approachLandSeconds);
            float t = Mathf.Clamp01((ttc - approachLandSeconds) / span);
            dist = dist * dist * (3f - 2f * dist);
            return dist * (t * t * (3f - 2f * t));
        }

        private CarArt _carArt;
        private bool _carArtTried;

        /// <summary>The drawn car's roof y (world) and height, or 0 without a car or its art.</summary>
        private float CarRoofAndHeight(out float roofY)
        {
            roofY = 0f;
            if (carView == null || carView.DrawnWidth <= 0f) return 0f;
            if (!_carArtTried) { _carArtTried = true; _carArt = CarArt.Load(); }
            if (_carArt == null || _carArt.StraightWidthPx <= 0f) return 0f;
            float h = _carArt.CarHeightPx * carView.DrawnWidth / _carArt.StraightWidthPx;
            roofY = carView.RearContact.y + h;
            return h;
        }

        private bool Place(int i, RoadRenderer.SegmentSample s, float bob, float pulse,
                           float anisotropy, float viewHalfWidth, float ppu)
        {
            float unit = s.halfWidth;                 // world units per road half-width here
            if (unit <= 0.0001f) return false;
            if (s.worldY < s.clipY - 0.001f) return false;   // behind a crest, like the props

            // Vertical size uses the projection's VERTICAL scale, exactly like the props.
            float vScale = unit / roadRenderer.roadHalfWidth * anisotropy;
            float heightUnits = heightMetres * RoadGeometry.SegmentLength * artScale;
            float spriteH = _sprite.rect.height / _sprite.pixelsPerUnit;
            float k = vScale * heightUnits / spriteH;
            float kMin = FloorPx(s.depth) / Mathf.Max(1f, ppu) / spriteH;
            if (k < kMin) k = kMin;
            float kBase = k;                          // before the cosmetic pulse
            k *= 1f + pulseAmount * pulse;
            // The float is cosmetic and perspective-scaled (k carries the depth); the
            // collision stays at the pickup's road position whatever this does.
            float bodyH = k * spriteH * PickupArt.BoltBodyHeight;
            // The hover is TRUE-perspective (vScale), never the size floor: lifted by the
            // inflated k, a far bolt's foot rode up to a farther, narrower row of road and a
            // side-lane bolt read as standing on the kerb or the grass (2026-10-07 frames).
            float lift = vScale * RoadGeometry.SegmentLength * artScale
                         * Mathf.Max(hoverMetres, 0.12f * heightMetres) + bobAmplitude * bodyH * bob;

            float halfW = _sprite.rect.width / _sprite.pixelsPerUnit * k * 0.5f;
            // While the size floor draws the bolt larger than perspective, its body is wider
            // than the road there by the difference: pull a side bolt in by exactly that
            // excess so its body stays on the asphalt. Zero near the car (perspective wins),
            // so the drawn lane and the collision lane agree where it is taken.
            float lat = _set.LateralOf(i);
            float kTrue = vScale * heightUnits / spriteH;
            float excess = (kBase - kTrue) * (_sprite.rect.width / _sprite.pixelsPerUnit) * 0.5f
                           * PickupArt.BoltBodyWidth / unit;
            if (excess > 0f) lat -= Mathf.Sign(lat) * Mathf.Min(Mathf.Abs(lat), excess);
            float x = s.worldX + lat * unit;

            // The approach lift: cosmetic, draw-only and PURELY VERTICAL. x above is final: the
            // unlifted on-road lane position, so the drop adds no horizontal motion at all
            // (it used to follow the lifted road row, which made a falling bolt slide sideways).
            // Size, collision and the sweep are untouched; the lift is exactly 0 once the car is
            // approachLandSeconds from the bolt (see ApproachEase), so it lands early and the
            // drawn bolt IS the collision position for the last second. Two parts:
            //  - a fraction of the bolt's own body (approachLift), and
            //  - CLEARANCE: enough to bring the body's foot up to the drawn car's roofline when
            //    the road row it stands on is at or below it, capped at approachMaxCarFraction
            //    of the car's height.
            // Never above the highest visible road row (beyond it is sky or a crest): that cap
            // is what keeps a lifted bolt from hanging in the air.
            if (approachLift > 0f)
            {
                float carSegs = (carView != null ? carView.distanceAhead : carDepthSegments * RoadGeometry.SegmentLength) / RoadGeometry.SegmentLength;
                float ease = ApproachEase(s.depth - _camFrac, CarSpeed, carSegs);
                if (ease > 0f)
                {
                    float bodyDraw = kBase * spriteH * PickupArt.BoltBodyHeight;
                    float foot0 = s.worldY + lift;
                    float want = approachLift * bodyDraw;
                    float roof;
                    float carH = CarRoofAndHeight(out roof);
                    if (carH > 0f)
                    {
                        float need = roof - approachClearBody * bodyDraw - foot0;
                        want = Mathf.Max(want, Mathf.Min(need, approachMaxCarFraction * carH));
                    }
                    want *= ease;
                    lift += Mathf.Min(want, Mathf.Max(0f, _topY - foot0));
                }
            }
            if (x + halfW < -viewHalfWidth || x - halfW > viewHalfWidth) return false;

            float fade = Mathf.Clamp01((drawDistance - s.depth) / (float)Mathf.Max(1, fadeSegments));
            // Barely hazed: the scenery fades into the distance, the thing to aim for does not.
            float fog = Mathf.Max(0.9f, Mathf.Clamp01(s.fog));

            Transform tr = _transforms[i];
            SpriteRenderer sr = _renderers[i];
            tr.localPosition = new Vector3(x, s.worldY + lift, 0f);
            tr.localScale = new Vector3(k, k, 1f);
            // r carries fog, a carries fade (PropSprite.shader), as for every prop.
            sr.color = CrestClip.Color(fog, fade, float.NegativeInfinity);
            sr.sortingOrder = sortingOrder;
            return true;
        }
    }
}
