using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// Tyre marks laid on the road during a genuine slide (plan item 4.3).
    ///
    /// Marks are stored in ROAD SPACE (x, z) in a fixed ring buffer and re-projected
    /// every frame, because the road itself reprojects as the camera moves - baking
    /// world positions would make the marks slide off the surface.
    ///
    /// One dynamic mesh, one material, one draw call. No GameObjects, no allocation,
    /// and a hard cap on geometry: the ring buffer cannot grow, so a long race costs
    /// exactly the same as a short one.
    /// </summary>
    public sealed class SkidMarks : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;
        [SerializeField] private Material skidMaterial;

        [Header("Emission")]
        [Tooltip("Marks are laid for real rear slip, not merely for steering input.")]
        [Range(0.01f, 0.6f)] public float slipThreshold = 0.06f;

        [Tooltip("Drift amount that also counts as laying rubber.")]
        [Range(0.1f, 1f)] public float driftThreshold = 0.40f;

        [Tooltip("Distance along the track between marks, world units. Only about 600 " +
                 "units of road are ever visible behind the car (see Rebuild), so this " +
                 "is what decides whether a trail reads as a line or as dashes.")]
        [Range(6f, 120f)] public float markSpacing = 40f;

        [Header("Geometry")]
        [Tooltip("Hard cap on stored marks (plan section 16 budget: 200). Only about " +
                 "600 units of road are ever visible behind the car, so a ring holding " +
                 "several times that is all the history that can be seen - the rest is " +
                 "vertices and per-frame work spent on marks that are off screen.")]
        [Range(32, 200)] public int maxMarks = 72;

        [Tooltip("Rear wheel lateral offset, normalised road units.")]
        [Range(0.05f, 0.5f)] public float wheelOffset = 0.22f;

        [Tooltip("Half width of a tyre mark, normalised road units. A real tyre is " +
                 "about 2% of a road's width, and at the size this band occupies on a " +
                 "phone that is invisible - so this is deliberately a caricature.")]
        [Range(0.01f, 0.2f)] public float wheelHalfWidth = 0.085f;

        [Tooltip("Darkness of a full-strength mark. Multiplies the rubber texture's own " +
                 "alpha, so the road stays visible through the marks.")]
        [Range(0f, 1f)] public float maxOpacity = 0.85f;

        [Header("Texture (Phase 10)")]
        [Tooltip("Single-tyre rubber strip, repeating along the trail. One strip per " +
                 "rear wheel is drawn, so this must be ONE tyre - a twin-track texture " +
                 "here would draw four lines.")]
        public string texturePath = "FX/skid_strip";

        [Tooltip("World units of trail per repeat of the strip texture (about 4x the " +
                 "strip's width keeps the rubber grain in proportion).")]
        public float textureTile = 750f;

        [Tooltip("Depth in front of the camera beyond which a mark is not drawn. Marks " +
                 "are laid at the car's VISUAL position, so nothing further away than " +
                 "the car can be one it left behind. Keep it at or just above " +
                 "CarView.distanceAhead.")]
        [Range(200f, 4000f)] public float maxDrawDistance = 1500f;

        private struct Mark
        {
            public float x;          // car centre, normalised
            public float z;          // absolute distance along the track
            public float strength;   // 0..1
            public bool startsStrip; // first mark of a new trail - do not join backwards
        }

        private Mark[] _ring;
        private int _head;      // next write slot
        private int _count;
        private float _lastX, _lastZ;
        private bool _hasLast;

        private Mesh _mesh;
        private Vector3[] _verts;
        private Color32[] _colors;
        private Vector2[] _uvs;
        private int[] _indices;
        private bool _textured;
        private int _vertCount, _quadCount;

        private const int QuadsPerPair = 2;   // one strip per rear wheel

        public int MarkCount { get { return _count; } }
        public int LastQuadCount { get { return _quadCount; } }

        // ---- diagnostics (plan 10.6) ------------------------------------------------
        // "72 marks, 72 quads" was true in every build while the road behind the car
        // stayed clean, so the counters that existed could not tell a trail that is not
        // BUILT from one that is not SEEN. These say where the geometry went and how
        // opaque it came out, which is the difference between the two. All of it is
        // arithmetic already being done in Rebuild; nothing here allocates.

        /// <summary>Quads whose vertices landed inside the camera's view, last rebuild.</summary>
        public int LastOnScreenQuads { get; private set; }

        /// <summary>Strongest vertex alpha written last rebuild, 0..1.</summary>
        public float LastPeakAlpha { get; private set; }

        /// <summary>Depth range in front of the camera actually emitted, world units.</summary>
        public float LastNearDepth { get; private set; }
        public float LastFarDepth { get; private set; }

        /// <summary>Vertical screen span the trail occupied, world units (y up).</summary>
        public float LastMinY { get; private set; }
        public float LastMaxY { get; private set; }

        /// <summary>Pairs rejected, by reason, last rebuild.</summary>
        public int LastCutByStripBreak { get; private set; }
        public int LastCutByNear { get; private set; }
        public int LastCutByFar { get; private set; }
        public int LastCutByProjection { get; private set; }

        /// <summary>True if the rubber strip texture was found and bound.</summary>
        public bool Textured { get { return _textured; } }

        private void Awake()
        {
            Initialise(skidMaterial);
        }

        public void Initialise(Material material)
        {
            _ring = new Mark[maxMarks];
            _head = 0;
            _count = 0;

            _mesh = new Mesh();
            _mesh.name = "SkidMarks";
            _mesh.MarkDynamic();
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

            int maxQuads = maxMarks * QuadsPerPair;
            _verts = new Vector3[maxQuads * 4];
            _colors = new Color32[maxQuads * 4];
            _uvs = new Vector2[maxQuads * 4];
            _indices = new int[maxQuads * 6];
            for (int q = 0; q < maxQuads; q++)
            {
                int v = q * 4, i = q * 6;
                _indices[i + 0] = v + 0; _indices[i + 1] = v + 1; _indices[i + 2] = v + 2;
                _indices[i + 3] = v + 0; _indices[i + 4] = v + 2; _indices[i + 5] = v + 3;
            }

            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            mf.sharedMesh = _mesh;

            MeshRenderer mr = GetComponent<MeshRenderer>();
            if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
            Texture2D tex = string.IsNullOrEmpty(texturePath) ? null : Resources.Load<Texture2D>(texturePath);
            if (tex != null && material != null)
            {
                material = new Material(material) { name = "SkidMarks (textured)" };
                material.mainTexture = tex;
                tex.wrapMode = TextureWrapMode.Repeat;
                _textured = true;
            }
            mr.sharedMaterial = material;
            // Explicit, so the draw order is a decision rather than a coincidence. The
            // road mesh and this mesh both sit at world z = 0 and both use sorting order
            // 0 by default, which leaves the tie to be broken by scene order. The
            // material's render queue (2600) already put us after the opaque road, but
            // relying on that alone means a material tweak can silently hide the marks.
            // Above the road (0), below the roadside props (10+) and the car (100).
            mr.sortingOrder = 5;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>
        /// Record slip for this frame. <paramref name="lateralSlip"/> is |lateralVel|
        /// normalised to the profile's cap.
        /// </summary>
        public void Record(float carX, float carZ, float lateralSlip, float driftAmount,
                           float speedPct, bool offRoad)
        {
            if (_ring == null) return;

            // Rubber is laid on tarmac, not on grass. Without this the car scrawls
            // skid marks across the field every time it runs wide, which reads as a
            // bug rather than as feedback.
            bool sliding = !offRoad && (lateralSlip > slipThreshold || driftAmount > driftThreshold);
            if (!sliding || speedPct < 0.08f)
            {
                _hasLast = false;      // break the strip so it does not bridge a gap
                return;
            }

            float strength = Mathf.Clamp01(Mathf.Max(lateralSlip, driftAmount * 0.8f))
                           * Mathf.Clamp01(speedPct * 1.4f);

            if (!_hasLast)
            {
                Emit(carX, carZ, strength, true);
                _lastX = carX; _lastZ = carZ; _hasLast = true;
                return;
            }

            float travelled = carZ - _lastZ;
            if (travelled < markSpacing) return;

            // FILL THE GAP, rather than laying one mark per frame.
            //
            // At racing speed the car covers 200 units in a frame and markSpacing is
            // tens - so consecutive marks land hundreds of units apart, not markSpacing
            // apart, and how far apart depends on the FRAME RATE. Laying one per frame
            // therefore made the trail coarser at 30 fps than at 60, and left gaps wide
            // enough that Rebuild's old strip-break heuristic threw every pair away.
            // Walking the distance actually travelled makes the trail identical at any
            // frame rate, and it follows the path the car took rather than cutting the
            // corner between two samples.
            int steps = Mathf.Min(Mathf.FloorToInt(travelled / markSpacing), _ring.Length);
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Emit(Mathf.Lerp(_lastX, carX, t), _lastZ + travelled * t, strength, false);
            }

            _lastX = carX;
            _lastZ = carZ;
        }

        private void Emit(float x, float z, float strength, bool startsStrip)
        {
            Mark m;
            m.x = x;
            m.z = z;
            m.strength = strength;
            m.startsStrip = startsStrip;

            _ring[_head] = m;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        /// <summary>Rebuild the visible mesh. Allocation-free.</summary>
        public void Rebuild()
        {
            Rebuild(0f);
        }

        /// <summary>
        /// Rebuild, meeting the car where it is DRAWN. The car is seated on its own piece
        /// of road (plan 10.1), so on a hill it sits a little above or below the road
        /// surface drawn at its depth. The newest marks take that offset in full and it
        /// fades to nothing toward the camera, so the trail starts at the tyres and still
        /// lies on the road behind them. <paramref name="carDepth"/> 0 disables it.
        /// </summary>
        public void Rebuild(float carDepth)
        {
            _vertCount = 0;
            _quadCount = 0;
            LastOnScreenQuads = 0;
            LastPeakAlpha = 0f;
            // Zero, not float.MaxValue: these are read by a debug overlay, and a
            // sentinel printed as 340282300000000000000000000000000000000 wraps the
            // panel across half the screen every frame the car is not sliding.
            LastNearDepth = 0f;
            LastFarDepth = 0f;
            LastMinY = 0f;
            LastMaxY = 0f;
            LastCutByStripBreak = 0;
            LastCutByNear = 0;
            LastCutByFar = 0;
            LastCutByProjection = 0;
            if (_ring == null || _mesh == null || roadRenderer == null || _count < 2)
            {
                if (_mesh != null) _mesh.Clear(false);
                return;
            }

            float camZ = roadRenderer.CameraZ;

            // Nothing beyond the car itself can be a skid mark it has already laid, and
            // the ring holds several times more road than the view can ever show. A
            // float compare per pair keeps the projection cost proportional to what is
            // actually drawn rather than to the buffer size.
            float drawDistance = maxDrawDistance;
            float seat = roadRenderer.PlayerSeatOffset;
            float nearDepth = RoadGeometry.CameraDepth(roadRenderer.fieldOfView) * roadRenderer.cameraHeight;

            // Walk oldest -> newest so consecutive marks are adjacent in the ring.
            int start = (_head - _count + _ring.Length) % _ring.Length;
            for (int k = 0; k < _count - 1; k++)
            {
                Mark a = _ring[(start + k) % _ring.Length];
                Mark b = _ring[(start + k + 1) % _ring.Length];

                // Where the trail was broken - the car stopped sliding, or left the
                // road - the new strip must not be joined back to the old one. The mark
                // itself says so now. It used to be guessed from the z gap, with a
                // threshold of markSpacing * 3; since the car covers several hundred
                // units between frames at racing speed and marks were laid one per
                // frame, that test discarded EVERY pair and the mesh came out empty
                // even once the marks were being placed correctly (plan 8.19.8).
                if (b.startsStrip) { LastCutByStripBreak++; continue; }

                float dzA = a.z - camZ;
                float dzB = b.z - camZ;
                if (dzB <= dzA) continue;
                if (dzB < 40f) { LastCutByNear++; continue; }    // behind / on top of the camera
                if (dzA > drawDistance) { LastCutByFar++; continue; }  // further up the road than the car

                Vector3 pa, pb;
                float sa, sb;
                if (!roadRenderer.TryProjectRoadPoint(a.x, dzA, out pa, out sa)) { LastCutByProjection++; continue; }
                if (!roadRenderer.TryProjectRoadPoint(b.x, dzB, out pb, out sb)) { LastCutByProjection++; continue; }

                if (LastNearDepth <= 0f || dzA < LastNearDepth) LastNearDepth = dzA;
                if (dzB > LastFarDepth) LastFarDepth = dzB;
                if (carDepth > 0f)
                {
                    pa.y += seat * Mathf.Clamp01((dzA - nearDepth) / Mathf.Max(1f, carDepth - nearDepth));
                    pb.y += seat * Mathf.Clamp01((dzB - nearDepth) / Mathf.Max(1f, carDepth - nearDepth));
                }

                // Fade with age so the trail tapers off instead of ending abruptly.
                float age = (float)k / Mathf.Max(1, _count - 1);
                float alphaA = a.strength * maxOpacity * Mathf.Clamp01(age * 1.6f);
                float alphaB = b.strength * maxOpacity * Mathf.Clamp01((age + 0.02f) * 1.6f);

                // scale = road half width in world units at that depth, so a normalised
                // offset converts to world units by multiplying.
                // V runs along the trail in world units, so the rubber grain stays put on
                // the road as the camera moves, and never restarts per quad.
                float va = Mathf.Repeat(a.z, textureTile * 64f) / textureTile;
                float vb = va + (b.z - a.z) / textureTile;
                EmitStrip(pa, sa, alphaA, va, pb, sb, alphaB, vb, -wheelOffset);
                EmitStrip(pa, sa, alphaA, va, pb, sb, alphaB, vb, +wheelOffset);
            }

            UploadMesh();
        }

        private void EmitStrip(Vector3 pa, float scaleA, float alphaA, float va,
                               Vector3 pb, float scaleB, float alphaB, float vb, float offset)
        {
            if (_quadCount >= _indices.Length / 6) return;

            float axc = pa.x + offset * scaleA;
            float bxc = pb.x + offset * scaleB;
            float aw = wheelHalfWidth * scaleA;
            float bw = wheelHalfWidth * scaleB;

            // Textured: the texture carries the rubber colour. Flat: dark rubber tint.
            byte grey = _textured ? (byte)255 : (byte)20;
            Color32 ca = new Color32(grey, grey, grey, (byte)(Mathf.Clamp01(alphaA) * 255f));
            Color32 cb = new Color32(grey, grey, grey, (byte)(Mathf.Clamp01(alphaB) * 255f));

            int v = _vertCount;
            _verts[v + 0] = new Vector3(axc - aw, pa.y, 0f);
            _verts[v + 1] = new Vector3(axc + aw, pa.y, 0f);
            _verts[v + 2] = new Vector3(bxc + bw, pb.y, 0f);
            _verts[v + 3] = new Vector3(bxc - bw, pb.y, 0f);

            if (_quadCount == 0) { LastMinY = LastMaxY = pa.y; }
            LastMinY = Mathf.Min(LastMinY, Mathf.Min(pa.y, pb.y));
            LastMaxY = Mathf.Max(LastMaxY, Mathf.Max(pa.y, pb.y));
            float peak = Mathf.Max(Mathf.Clamp01(alphaA), Mathf.Clamp01(alphaB));
            if (peak > LastPeakAlpha) LastPeakAlpha = peak;
            Camera cam = Camera.main;
            if (cam != null)
            {
                float halfH = cam.orthographicSize, halfW = halfH * cam.aspect;
                Vector3 c = cam.transform.position;
                float lo = Mathf.Min(pa.y, pb.y), hi = Mathf.Max(pa.y, pb.y);
                float left = Mathf.Min(axc - aw, bxc - bw), right = Mathf.Max(axc + aw, bxc + bw);
                if (hi >= c.y - halfH && lo <= c.y + halfH
                    && right >= c.x - halfW && left <= c.x + halfW) LastOnScreenQuads++;
            }
            _colors[v + 0] = ca;
            _colors[v + 1] = ca;
            _colors[v + 2] = cb;
            _colors[v + 3] = cb;
            _uvs[v + 0] = new Vector2(0f, va);
            _uvs[v + 1] = new Vector2(1f, va);
            _uvs[v + 2] = new Vector2(1f, vb);
            _uvs[v + 3] = new Vector2(0f, vb);

            _vertCount += 4;
            _quadCount++;
        }

        private void UploadMesh()
        {
            _mesh.Clear(false);
            if (_quadCount == 0) return;
            _mesh.SetVertices(_verts, 0, _vertCount);
            _mesh.SetColors(_colors, 0, _vertCount);
            _mesh.SetUVs(0, _uvs, 0, _vertCount);
            _mesh.SetIndices(_indices, 0, _quadCount * 6, MeshTopology.Triangles, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 1f));
        }

        /// <summary>Wipe the trail. Used on race restart.</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
            _hasLast = false;
            if (_mesh != null) _mesh.Clear(false);
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
