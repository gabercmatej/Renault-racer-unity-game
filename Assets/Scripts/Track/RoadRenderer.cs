using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// Builds the pseudo-3D road as ONE procedural mesh, every frame.
    ///
    /// All buffers are pre-allocated and reused, so a racing frame allocates 0 bytes
    /// (plan sections 11 and 17.5). Projection runs in the reference racer's
    /// virtual-screen space (pixels, +y down) and converts to world units on emit.
    ///
    /// PHASE 10 - textured, still one mesh. The projection, the hill clip and the
    /// near-to-far walk are unchanged; what changed is what each band is drawn with:
    ///
    ///   sub-mesh 0  ground   grass texture, repeat, mips
    ///   sub-mesh 1  asphalt  asphalt texture, repeat, mips
    ///   sub-mesh 2  curbs    black/yellow curb texture, repeat along the track
    ///   sub-mesh 3  edge lines    (alpha blended, repeat along the track)
    ///   sub-mesh 4  centre dashes (alpha blended, one dash per 3-segment group)
    ///   sub-mesh 5  finish strip  (checker, repeat, starts exactly at the gate)
    ///   sub-mesh 6  fence    guardrail, set BACK from the barrier in the run-off
    ///   sub-mesh 7  barrier  tyre wall ON the collision barrier line
    ///
    /// The last two are alpha blended and drawn FAR to NEAR, so a near module covers a
    /// far one; the fence is a lower sub-mesh than the barrier, so the tyre wall always
    /// draws over the guardrail standing behind it.
    ///
    /// THE IMPACT ZONE IS A TYRE WALL. A steel guardrail alone on a wide green run-off
    /// read as roadside furniture rather than as the thing the car hits, and it is not
    /// what a circuit puts at the edge of a racing surface. The barrier the collision
    /// actually uses is now a tyre wall - stacked, painted, unmistakably an impact zone
    /// - and the guardrail moves OUT into the run-off, giving the empty grass a second
    /// line of depth. Neither moves a collision boundary: the tyre wall is drawn on
    /// exactly the limit the simulation tests, and the fence is decoration beyond it.
    ///
    /// Eight materials on one renderer: eight draw calls instead of one, but every
    /// pixel is sampled once - fill rate, not draw calls, is what a weak phone runs out of.
    /// Texture coordinates carry a projective q so the texture does not bend along the
    /// quad diagonals (see RoadUnlit.shader). Anything whose texture is missing falls
    /// back to flat vertex colour, so the game still draws with no art at all.
    ///
    /// Gameplay data is only READ: barrier limits place the rails, segment 0 is the
    /// finish gate. Nothing here can move a collision boundary or the gate.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class RoadRenderer : MonoBehaviour
    {
        [Header("Projection")]
        [Tooltip("Vertical field of view in degrees. 100 matches the reference framing.")]
        [Range(60f, 140f)] public float fieldOfView = RoadGeometry.FieldOfView;

        [Tooltip("Camera height above the road surface, world units.")]
        public float cameraHeight = RoadGeometry.CameraHeight;

        [Tooltip("Segments drawn ahead of the camera.")]
        [Range(50, 600)] public int drawDistance = 300;

        [Tooltip("Half width of the road, world units.")]
        public float roadHalfWidth = RoadGeometry.RoadHalfWidth;

        [Header("Framing")]
        [Tooltip("Virtual screen height in pixels. Sets the projection's vertical scale.")]
        public float virtualHeight = 720f;

        [Tooltip("Aspect the layout is authored for. Wider screens reveal more periphery " +
                 "rather than stretching the road.")]
        public float referenceAspect = 16f / 9f;

        [Header("Appearance")]
        [Tooltip("Road width multiplier for the curbs.")]
        [Range(1f, 1.6f)] public float rumbleScale = 1.15f;

        [Tooltip("Centre dash width as a fraction of the road half width.")]
        [Range(0.005f, 0.08f)] public float laneWidth = 0.02f;

        [Tooltip("Distance fog density. 0 disables fog.")]
        [Range(0f, 8f)] public float fogDensity = 3.5f;

        [Tooltip("Colour the far road fades into. Should match the bottom of the " +
                 "background panorama so the horizon has no seam.")]
        public Color hazeColor = new Color(0.72f, 0.82f, 0.92f);

        // Flat-colour fallbacks, used only where a texture is missing.
        public Color skyColor    = new Color(0.28f, 0.60f, 0.86f);
        public Color roadLight   = new Color(0.42f, 0.42f, 0.42f);
        public Color roadDark    = new Color(0.39f, 0.39f, 0.39f);
        public Color grassLight  = new Color(0.10f, 0.62f, 0.14f);
        public Color grassDark   = new Color(0.07f, 0.54f, 0.11f);
        [Tooltip("Curb fallback colours. Black and yellow: the Phase 10 curbs are never red/white.")]
        public Color rumbleLight = new Color(1.00f, 0.80f, 0.00f);
        public Color rumbleDark  = new Color(0.07f, 0.07f, 0.09f);
        public Color laneColor   = new Color(0.90f, 0.90f, 0.90f);

        [Header("Textures (Phase 10)")]
        [Tooltip("Resources paths. A missing texture falls back to flat colour.")]
        public string grassTexturePath   = "Track/grass";
        public string asphaltTexturePath = "Track/asphalt";
        public string curbTexturePath    = "Track/curb";
        public string lineTexturePath    = "Track/marking_line";
        public string dashTexturePath    = "Track/marking_dash";
        public string finishTexturePath  = "Track/finish_checker";
        [Tooltip("The near barrier, drawn ON the collision limit: the tyre wall.")]
        public string railTexturePath    = "Environment/tyrewall";
        [Tooltip("The outer decorative line, drawn back in the run-off: the guardrail.")]
        public string fenceTexturePath   = "Environment/guardrail";

        [Tooltip("World units covered by one repeat of the grass texture. Above ~1650 " +
                 "the blades start to read as giant.")]
        public float grassTile = 1100f;
        [Tooltip("World units covered by one repeat of the asphalt texture.")]
        public float asphaltTile = 1100f;
        [Tooltip("World units along the track per curb repeat (one yellow + one black " +
                 "stripe): 1200 keeps the 600-unit stripe rhythm of the old rumble strip.")]
        public float curbRepeat = 1200f;
        [Tooltip("Brightness of alternate 3-segment groups on the ground. A faint motion cue; 1 = none.")]
        [Range(0.8f, 1f)] public float groundBanding = 0.96f;

        [Header("Markings (Phase 10)")]
        [Tooltip("Edge line centre and PAINTED width, in road half-widths. The line " +
                 "texture paints only the middle half of its height, so the quad is twice " +
                 "this wide. 0 width = off.")]
        [Range(0.5f, 1f)] public float edgeLineCentre = 0.95f;
        [Range(0f, 0.1f)] public float edgeLineWidth = 0.035f;
        [Tooltip("World units along the track per repeat of the edge line texture.")]
        public float edgeLineRepeat = 800f;
        [Tooltip("Finish strip depth along the track from the gate, world units.")]
        public float finishDepth = 733f;
        [Tooltip("Checker texture repeats across the road width and along the strip " +
                 "(each repeat is 2x2 cells: 6 x 2 gives 12 x 4 square cells).")]
        public Vector2 finishRepeats = new Vector2(6f, 2f);

        [Header("Barriers (Phase 10)")]
        [Tooltip("Draw the tyre wall at the collision barriers.")]
        public bool drawRails = true;
        [Tooltip("Height of the tyre wall, world units. A barrier strip is stretched to " +
                 "this height and to railModule along the track, and those two are NOT " +
                 "in the same units on screen: height uses the vertical projection " +
                 "scale, while a module's on-screen length is the wall sweeping away " +
                 "from the camera - which near the car is mostly SIDEWAYS, because the " +
                 "barrier sits two and a half road widths out. The ratio that draws the " +
                 "strip undistorted is a property of the projection, not of the art: " +
                 "height = 1.69 * module * (texture aspect). prepare_barriers.py prints " +
                 "the pair, so the two cannot drift apart - change it there, not here.\n\n" +
                 "523 over a module of 600 is 4 tyres per 3 m of track, a tyre at 0.75 m, " +
                 "and a wall three tyres tall. The previous pair (420 over 400) drew the " +
                 "OLD texture, whose tyres were face-on: a chain of rings with bright " +
                 "sidewalls, which is exactly what tire_notes.txt.txt says not to do.")]
        public float railHeight = 523f;
        [Tooltip("World units along the track per repeat of the tyre wall. Three " +
                 "segments, so the tyres never drift against the segment grid.")]
        public float railModule = 600f;
        [Tooltip("How far OUTSIDE the collision limit the rail face is drawn, in road " +
                 "half-widths. Collision is tested on the car's CENTRE, so the body touches " +
                 "the barrier half a car-width outside that limit. CarView sets this to " +
                 "half the car's drawn width, so the wall is exactly where the body stops.")]
        public float railOutset = 0.31f;

        [Tooltip("Sorting order of the tyre wall. THE WALL IS NOT PART OF THE ROAD MESH, " +
                 "and this is the whole reason why.\n\n" +
                 "The road mesh is one MeshRenderer whose materials sit in render queues " +
                 "2000-2407, while every sprite in the game - spectators, signs, trees, " +
                 "the car - is in the Transparent queue at 3000. A render queue beats a " +
                 "sorting order, so EVERY prop drew over the wall no matter where it " +
                 "stood, and a spectator's feet and shins appeared painted on top of the " +
                 "tyres. A MeshRenderer has one sorting order for all its sub-meshes, so " +
                 "the wall could not be re-ordered while it lived inside the road mesh.\n\n" +
                 "It is now its own renderer in queue 3000, ordered by this. 95 is above " +
                 "RoadsideSprites.sortingOrderCeiling (90) and below the car (100), which " +
                 "is exactly right: EVERY roadside prop stands outside the barrier line " +
                 "the wall is drawn on, so the wall is always between the camera and the " +
                 "prop and must always occlude its base - while the car is always inside " +
                 "that line and must never be occluded by it.")]
        public int wallSortingOrder = 95;

        [Tooltip("Draw the guardrail out in the run-off, behind the tyre wall.")]
        public bool drawFence = true;
        [Tooltip("How far beyond the tyre wall the guardrail stands, in road half-widths.")]
        [Range(0f, 6f)] public float fenceOutset = 1.9f;
        [Tooltip("Height of the guardrail, world units. Left at the value the rail was " +
                 "drawn at before it moved out here, which is the proportion that was " +
                 "already on screen and looked like a guardrail.")]
        public float fenceHeight = 410f;
        [Tooltip("World units along the track per repeat of the guardrail texture. " +
                 "400 = two segments, so the posts never drift against the segment grid.")]
        public float fenceModule = 400f;

        [Header("Player framing (plan section 10.1)")]
        [Tooltip("How much of the elevation change ahead the CAR is allowed to follow. " +
                 "0 seats the car on its own piece of road and never moves it; 1 is the " +
                 "old behaviour, which threw the car off the bottom of the screen on a " +
                 "steep descent. A small value keeps hills readable on the car itself. " +
                 "Raised from 0.15 with the authored elevation: with hills that are " +
                 "actually visible, a car that ignores them sits off its own road over a " +
                 "crest. playerScreenTop is still the hard guarantee underneath it.")]
        [Range(0f, 1f)] public float playerElevationFollow = 0.25f;

        [Tooltip("Highest the car's road-contact point may sit, as a fraction of the " +
                 "screen height (0 = top). The hard guarantee that the car stays trackable.")]
        [Range(0.3f, 0.95f)] public float playerScreenTop = 0.72f;

        [Tooltip("Lowest the car's road-contact point may sit, as a fraction of the " +
                 "screen height (1 = bottom).")]
        [Range(0.3f, 0.99f)] public float playerScreenBottom = 0.88f;

        // --- runtime state -----------------------------------------------------
        private TrackModel _track;
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Camera _camera;

        // The tyre wall is drawn by a renderer of its OWN, not as a sub-mesh of the road.
        // See wallSortingOrder for why.
        private Mesh _wallMesh;
        private MeshRenderer _wallRenderer;
        private Vector3[] _wallVerts;
        private Color32[] _wallColors;
        private Vector3[] _wallUvq;
        private int[] _wallIdx;
        private int _wallVertCount, _wallIdxCount;

        private float _cameraZ;
        private float _cameraX;

        private const int SubGround = 0, SubAsphalt = 1, SubCurb = 2, SubLine = 3, SubDash = 4,
                          SubFinish = 5, SubFence = 6, SubRail = 7;
        private const int SubCount = 8;

        // Upper bound of quads one segment can emit, per sub-mesh.
        private static readonly int[] QuadsPerSegment = { 2, 1, 2, 2, 1, 1, 2, 2 };

        private Vector3[] _verts;
        private Color32[] _colors;
        private Vector3[] _uvq;
        private int _vertCount;
        private readonly int[][] _idx = new int[SubCount][];
        private readonly int[] _idxCount = new int[SubCount];

        // Barriers are collected near-to-far and emitted reversed, so a near module
        // covers a far one. The fence and the tyre wall share one buffer and each quad
        // carries the sub-mesh it belongs to: they are interleaved in a consistent
        // order, so reversing the whole buffer once orders BOTH sub-meshes correctly.
        private Vector3[] _railVerts;
        private Color32[] _railColors;
        private Vector3[] _railUvq;
        private int[] _railSub;
        private int _railQuads;

        private int _capacitySegments;
        private int _quadCount;
        private float _grassExtent;
        private readonly bool[] _textured = new bool[SubCount];

        // Projection constants for the current frame.
        private float _vw, _vh, _ppu, _camDepth;

        public struct SegmentSample
        {
            public int   index;      // track segment index
            public int   depth;      // SEGMENTS AHEAD OF THE CAMERA - see below
            public float worldX;     // road centre at this segment, world units
            public float worldY;
            public float halfWidth;  // projected road half width, world units
            public float fog;        // 1 = clear, 0 = fully hazed
            public float clipY;      // world y of the nearer terrain's horizon: anything
                                     // whose base is BELOW this is hidden behind a crest
        }

        // WHY `depth` EXISTS, and it is the whole of the T07-T08 horizon fix.
        //
        // A sample's INDEX IN THIS LIST is not how far away it is. The list holds only the
        // samples that are currently VISIBLE, and a crest hides everything beyond it - so
        // the list is short while the camera climbs and long the instant it clears the
        // summit. At Z = 259,400, between T07 and T08, the eye crosses the high point and
        // the count goes from a few dozen to a few hundred in ONE FRAME.
        //
        // Nothing about the road changes at that moment. But every roadside prop's fade
        // and sorting order were computed from its index within that list and from the
        // list's own length, so all of them changed at once: props already on screen
        // jumped to full opacity, and a batch of distant ones appeared at full opacity
        // out of nothing. That is the pop, and it was never the panorama texture.
        //
        // Depth is the honest number - it does not know or care what is visible - so
        // presentation now reads it and the count is used for nothing but counting.

        private SegmentSample[] _samples;
        private int _sampleCount;

        public int SampleCount { get { return _sampleCount; } }
        public SegmentSample GetSample(int i) { return _samples[i]; }

        public int LastQuadCount { get { return _quadCount; } }
        public int LastTriangleCount { get { return _quadCount * 2; } }
        public TrackModel Track { get { return _track; } }

        public float CameraZ { get { return _cameraZ; } set { _cameraZ = value; } }
        public float CameraX { get { return _cameraX; } set { _cameraX = value; } }

        /// <summary>
        /// Vertical offset, world units, between where the player car is DRAWN (seated,
        /// plan 10.1) and where the road surface under it is drawn. Zero on the flat;
        /// non-zero on hills. Skid marks use it so the trail meets the tyres.
        /// </summary>
        public float PlayerSeatOffset { get; private set; }

        public void Initialise(TrackModel track, Material material)
        {
            _track = track;
            _camera = Camera.main;

            _mesh = new Mesh();
            _mesh.name = "RoadMesh";
            _mesh.MarkDynamic();
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _mesh.subMeshCount = SubCount;

            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = GetComponent<MeshRenderer>();
            _renderer.sharedMaterials = BuildMaterials(material);
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.allowOcclusionWhenDynamic = false;

            BuildWallRenderer(_renderer.sharedMaterials[SubRail]);
            AllocateBuffers();
        }

        /// <summary>
        /// The tyre wall's own renderer (see <see cref="wallSortingOrder"/>). It carries a
        /// copy of the rail material moved into the Transparent queue, so that it and the
        /// sprites are sorted against each other by sorting order rather than by queue.
        /// </summary>
        private void BuildWallRenderer(Material railMaterial)
        {
            _wallMesh = new Mesh();
            _wallMesh.name = "TyreWallMesh";
            _wallMesh.MarkDynamic();
            _wallMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            var go = new GameObject("TyreWall", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = _wallMesh;

            _wallRenderer = go.GetComponent<MeshRenderer>();
            if (railMaterial != null)
            {
                Material m = new Material(railMaterial);
                m.name = "TyreWall";
                // 3000 is the Transparent queue the sprite shaders use. Same queue as the
                // props and the car, so sorting order - not the queue - decides.
                m.renderQueue = 3000;
                _wallRenderer.sharedMaterial = m;
            }
            _wallRenderer.sortingOrder = wallSortingOrder;
            _wallRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _wallRenderer.receiveShadows = false;
            _wallRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _wallRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _wallRenderer.allowOcclusionWhenDynamic = false;
        }

        private Material[] BuildMaterials(Material baseMaterial)
        {
            var mats = new Material[SubCount];
            string[] paths = { grassTexturePath, asphaltTexturePath, curbTexturePath, lineTexturePath,
                               dashTexturePath, finishTexturePath, fenceTexturePath, railTexturePath };
            for (int i = 0; i < SubCount; i++)
            {
                // Copies of the scene's road material, so the shader is the one the build
                // already includes - no Shader.Find, which silently fails in a player.
                Material m = baseMaterial != null ? new Material(baseMaterial) : null;
                if (m == null) continue;
                m.name = "Road_" + i;
                Texture2D tex = string.IsNullOrEmpty(paths[i]) ? null : Resources.Load<Texture2D>(paths[i]);
                if (tex != null)
                {
                    m.mainTexture = tex;
                    _textured[i] = true;
                }
                m.SetColor("_Haze", hazeColor);
                bool blended = i == SubLine || i == SubDash || i == SubFence || i == SubRail;
                if (blended)
                {
                    m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    // Opaque bands first, then decals, then rails.
                    m.renderQueue = 2400 + i;
                }
                else
                {
                    m.renderQueue = 2000 + i;
                }
                mats[i] = m;
            }
            Debug.Log("[RoadRenderer] textures: grass " + _textured[SubGround] + ", asphalt " + _textured[SubAsphalt]
                      + ", curb " + _textured[SubCurb] + ", lines " + _textured[SubLine] + ", dashes " + _textured[SubDash]
                      + ", finish " + _textured[SubFinish] + ", fence " + _textured[SubFence]
                      + ", tyre wall " + _textured[SubRail]);
            return mats;
        }

        private void AllocateBuffers()
        {
            _capacitySegments = Mathf.Max(drawDistance, 50);
            int total = 0;
            for (int s = 0; s < SubCount; s++)
            {
                int quads = _capacitySegments * QuadsPerSegment[s];
                _idx[s] = new int[quads * 6];
                total += quads;
            }
            _verts  = new Vector3[total * 4];
            _colors = new Color32[total * 4];
            _uvq    = new Vector3[total * 4];

            int railQuads = _capacitySegments
                          * (QuadsPerSegment[SubRail] + QuadsPerSegment[SubFence]);
            _railVerts  = new Vector3[railQuads * 4];
            _railColors = new Color32[railQuads * 4];
            _railUvq    = new Vector3[railQuads * 4];
            _railSub    = new int[railQuads];

            // The wall's own mesh. Only SubRail quads land here, so the fence's share of
            // railQuads is headroom rather than waste.
            _wallVerts  = new Vector3[railQuads * 4];
            _wallColors = new Color32[railQuads * 4];
            _wallUvq    = new Vector3[railQuads * 4];
            _wallIdx    = new int[railQuads * 6];

            _samples = new SegmentSample[_capacitySegments];
        }

        /// <summary>
        /// Rebuild the road mesh for the current camera position.
        /// Allocation-free after Initialise.
        /// </summary>
        public void Rebuild(float aspect)
        {
            if (_track == null || _mesh == null) return;

            _quadCount = 0;
            _vertCount = 0;
            _railQuads = 0;
            _sampleCount = 0;
            for (int s = 0; s < SubCount; s++) _idxCount[s] = 0;

            _vh = virtualHeight;
            _vw = _vh * referenceAspect;
            float orthoSize = _camera != null ? _camera.orthographicSize : 5f;
            _ppu = _vh / (2f * orthoSize);
            _camDepth = RoadGeometry.CameraDepth(fieldOfView);

            _grassExtent = _vh * Mathf.Max(referenceAspect, aspect) * 0.5f + _vw;

            int segments = Mathf.Min(drawDistance, _capacitySegments);
            int baseIndex = _track.SegmentIndexAt(_cameraZ);
            float basePercent = _track.SegmentPercentAt(_cameraZ);
            float camY = _track.HeightAt(_cameraZ) + cameraHeight;
            float camXWorld = _cameraX * roadHalfWidth;
            float camZUnwrapped = baseIndex * RoadGeometry.SegmentLength
                                + basePercent * RoadGeometry.SegmentLength;

            float x = 0f;
            float dx = -(_track[baseIndex].curve * basePercent);
            float maxy = _vh;

            RoadGeometry.Projected p1 = default(RoadGeometry.Projected);

            for (int n = 0; n < segments; n++)
            {
                int idx = baseIndex + n;
                Segment seg = _track[idx];
                Segment next = _track[idx + 1];
                int wrapped = _track.Wrap(idx);

                float z1 = idx * RoadGeometry.SegmentLength;
                float z2 = z1 + RoadGeometry.SegmentLength;

                if (n == 0)
                    p1 = RoadGeometry.Project(x, seg.y, z1, camXWorld, camY, camZUnwrapped,
                                              _camDepth, _vw, _vh, roadHalfWidth);

                RoadGeometry.Projected p2 = RoadGeometry.Project(
                    x + dx, next.y, z2, camXWorld, camY, camZUnwrapped,
                    _camDepth, _vw, _vh, roadHalfWidth);

                float xStart = x, dxStart = dx;
                x += dx;
                dx += seg.curve;

                bool visible = RoadGeometry.BandVisible(p1, p2, _camDepth, maxy);

                if (visible)
                {
                    float fog1 = fogDensity > 0f ? RoadGeometry.Fog((float)n / drawDistance, fogDensity) : 1f;
                    float fog2 = fogDensity > 0f ? RoadGeometry.Fog((float)(n + 1) / drawDistance, fogDensity) : 1f;
                    bool light = ((wrapped / RoadGeometry.RumbleLength) & 1) == 0;

                    EmitBand(p1, p2, wrapped, z1, light, fog1, fog2, seg, next);

                    if (wrapped * RoadGeometry.SegmentLength < finishDepth)
                        EmitFinish(wrapped, xStart, dxStart, seg, next, z1, camXWorld, camY, camZUnwrapped, fog1);

                    Vector3 w = ToWorld(p1.x, p1.y);
                    _samples[_sampleCount].index     = wrapped;
                    _samples[_sampleCount].depth     = n;
                    _samples[_sampleCount].worldX    = w.x;
                    _samples[_sampleCount].worldY    = w.y;
                    _samples[_sampleCount].halfWidth = p1.w / _ppu;
                    _samples[_sampleCount].fog       = fog1;
                    _samples[_sampleCount].clipY     = ToWorld(0f, maxy).y;
                    _sampleCount++;

                    maxy = p2.y;
                }

                p1 = p2;
            }

            FlushRails();
            UploadMesh();
        }

        // --------------------------------------------------------------- bands

        private static float Q(RoadGeometry.Projected p) { return p.scale * 1000f; }

        private void EmitBand(RoadGeometry.Projected p1, RoadGeometry.Projected p2, int idx, float z1,
                              bool light, float fog1, float fog2, Segment seg, Segment next)
        {
            float edge = _grassExtent;
            float hw = roadHalfWidth;
            float q1 = Q(p1), q2 = Q(p2);
            float segLen = RoadGeometry.SegmentLength;

            // ---- ground
            {
                bool tex = _textured[SubGround];
                Color c = tex ? Color.white * (light ? 1f : groundBanding) : (light ? grassLight : grassDark);
                c.a = 1f;
                float v1 = WrapAlong(idx, grassTile), v2 = v1 + segLen / grassTile;
                float r1 = p1.w * rumbleScale, r2 = p2.w * rumbleScale;
                // World lateral offset of a screen x on a row: (sx - p.x) / p.w * hw.
                float uOut1L = (-edge - p1.x) / p1.w * hw / grassTile, uIn1L = -rumbleScale * hw / grassTile;
                float uOut2L = (-edge - p2.x) / p2.w * hw / grassTile, uIn2L = uIn1L;
                Quad(SubGround,
                     -edge, p1.y, uOut1L, v1, q1,
                     p1.x - r1, p1.y, uIn1L, v1, q1,
                     p2.x - r2, p2.y, uIn2L, v2, q2,
                     -edge, p2.y, uOut2L, v2, q2,
                     c, fog1, fog2);
                float uOut1R = (edge - p1.x) / p1.w * hw / grassTile, uIn1R = rumbleScale * hw / grassTile;
                float uOut2R = (edge - p2.x) / p2.w * hw / grassTile;
                Quad(SubGround,
                     p1.x + r1, p1.y, uIn1R, v1, q1,
                     edge, p1.y, uOut1R, v1, q1,
                     edge, p2.y, uOut2R, v2, q2,
                     p2.x + r2, p2.y, uIn1R, v2, q2,
                     c, fog1, fog2);
            }

            // ---- curbs: U runs along the track, V across the curb (0 at the road edge)
            {
                bool tex = _textured[SubCurb];
                Color c = tex ? Color.white : (light ? rumbleLight : rumbleDark);
                float u1 = WrapAlong(idx, curbRepeat), u2 = u1 + segLen / curbRepeat;
                float r1 = p1.w * rumbleScale, r2 = p2.w * rumbleScale;
                Quad(SubCurb,
                     p1.x - r1, p1.y, u1, 1f, q1,
                     p1.x - p1.w, p1.y, u1, 0f, q1,
                     p2.x - p2.w, p2.y, u2, 0f, q2,
                     p2.x - r2, p2.y, u2, 1f, q2,
                     c, fog1, fog2);
                Quad(SubCurb,
                     p1.x + p1.w, p1.y, u1, 0f, q1,
                     p1.x + r1, p1.y, u1, 1f, q1,
                     p2.x + r2, p2.y, u2, 1f, q2,
                     p2.x + p2.w, p2.y, u2, 0f, q2,
                     c, fog1, fog2);
            }

            // ---- asphalt
            {
                bool tex = _textured[SubAsphalt];
                Color c = tex ? Color.white : (light ? roadLight : roadDark);
                if (!tex && seg.surface == Surface.Sand)
                    c = light ? new Color(0.85f, 0.76f, 0.50f) : new Color(0.80f, 0.71f, 0.45f);
                float v1 = WrapAlong(idx, asphaltTile), v2 = v1 + segLen / asphaltTile;
                float u = hw / asphaltTile;
                Quad(SubAsphalt,
                     p1.x - p1.w, p1.y, -u, v1, q1,
                     p1.x + p1.w, p1.y,  u, v1, q1,
                     p2.x + p2.w, p2.y,  u, v2, q2,
                     p2.x - p2.w, p2.y, -u, v2, q2,
                     c, fog1, fog2);
            }

            // ---- edge lines: U along the track, V across. Paint is the middle half of V.
            if (edgeLineWidth > 0f)
            {
                Color c = _textured[SubLine] ? Color.white : laneColor;
                float w = _textured[SubLine] ? edgeLineWidth : edgeLineWidth * 0.5f;
                float a0 = edgeLineCentre - w, a1 = edgeLineCentre + w;
                float u1 = WrapAlong(idx, edgeLineRepeat), u2 = u1 + segLen / edgeLineRepeat;
                Quad(SubLine,
                     p1.x - p1.w * a1, p1.y, u1, 0f, q1,
                     p1.x - p1.w * a0, p1.y, u1, 1f, q1,
                     p2.x - p2.w * a0, p2.y, u2, 1f, q2,
                     p2.x - p2.w * a1, p2.y, u2, 0f, q2,
                     c, fog1, fog2);
                Quad(SubLine,
                     p1.x + p1.w * a0, p1.y, u1, 1f, q1,
                     p1.x + p1.w * a1, p1.y, u1, 0f, q1,
                     p2.x + p2.w * a1, p2.y, u2, 0f, q2,
                     p2.x + p2.w * a0, p2.y, u2, 1f, q2,
                     c, fog1, fog2);
            }
            // ---- centre dash: one dash texture across a light 3-segment group.
            if (light && laneWidth > 0f)
            {
                Color c = _textured[SubDash] ? Color.white : laneColor;
                int g = RoadGeometry.RumbleLength;
                float f1 = (float)(idx % g) / g, f2 = f1 + 1f / g;
                float lw = _textured[SubDash] ? laneWidth * 2f : laneWidth;   // paint = middle half
                float l1 = p1.w * lw, l2 = p2.w * lw;
                Quad(SubDash,
                     p1.x - l1, p1.y, f1, 0f, q1,
                     p1.x + l1, p1.y, f1, 1f, q1,
                     p2.x + l2, p2.y, f2, 1f, q2,
                     p2.x - l2, p2.y, f2, 0f, q2,
                     c, fog1, fog2);
            }

            // ---- barriers. The guardrail goes down FIRST, so that when the whole
            // buffer is reversed the fence still lands in the lower sub-mesh and the
            // tyre wall in front of it draws over it.
            float limL = Mathf.Abs(seg.barrierLeft), limLn = Mathf.Abs(next.barrierLeft);
            float limR = seg.barrierRight, limRn = next.barrierRight;

            if (drawFence)
            {
                float o = railOutset + fenceOutset;
                EmitBarrier(SubFence, fenceHeight, fenceModule, p1, p2,
                            -(limL + o), -(limLn + o), idx, fog1, fog2);
                EmitBarrier(SubFence, fenceHeight, fenceModule, p1, p2,
                            limR + o, limRn + o, idx, fog1, fog2);
            }
            if (drawRails)
            {
                EmitBarrier(SubRail, railHeight, railModule, p1, p2,
                            -(limL + railOutset), -(limLn + railOutset), idx, fog1, fog2);
                EmitBarrier(SubRail, railHeight, railModule, p1, p2,
                            limR + railOutset, limRn + railOutset, idx, fog1, fog2);
            }
        }

        /// <summary>
        /// The finish strip. Its NEAR edge is the gate itself: segment 0 starts at z = 0,
        /// which is where RaceSession ends the lap (z >= track length). It extends
        /// finishDepth units beyond the line, sub-segment, so the strip keeps its authored
        /// depth instead of snapping to 200-unit segments.
        /// </summary>
        private void EmitFinish(int wrapped, float xStart, float dxStart, Segment seg, Segment next,
                                float z1, float camX, float camY, float camZ, float fog)
        {
            float segLen = RoadGeometry.SegmentLength;
            float from = wrapped * segLen;                    // distance past the gate at this segment's start
            float t1 = Mathf.Clamp01((finishDepth - from) / segLen);
            if (t1 <= 0f) return;

            RoadGeometry.Projected a = ProjectWithin(xStart, dxStart, seg, next, z1, 0f, camX, camY, camZ);
            RoadGeometry.Projected b = ProjectWithin(xStart, dxStart, seg, next, z1, t1, camX, camY, camZ);
            if (a.camZ <= _camDepth || b.y >= a.y) return;

            float va = finishRepeats.y * from / finishDepth;
            float vb = finishRepeats.y * Mathf.Min(1f, (from + t1 * segLen) / finishDepth);
            Color c = _textured[SubFinish] ? Color.white : new Color(0.92f, 0.92f, 0.9f);
            Quad(SubFinish,
                 a.x - a.w, a.y, 0f, va, Q(a),
                 a.x + a.w, a.y, finishRepeats.x, va, Q(a),
                 b.x + b.w, b.y, finishRepeats.x, vb, Q(b),
                 b.x - b.w, b.y, 0f, vb, Q(b),
                 c, fog, fog);
        }

        private RoadGeometry.Projected ProjectWithin(float xStart, float dxStart, Segment seg, Segment next,
                                                     float z1, float t, float camX, float camY, float camZ)
        {
            // Within a segment the centre line moves linearly (x += dx per segment) and
            // the surface height interpolates linearly - the same model the road uses.
            float y = RoadGeometry.Interpolate(seg.y, next.y, t);
            return RoadGeometry.Project(xStart + dxStart * t, y, z1 + RoadGeometry.SegmentLength * t,
                                        camX, camY, camZ, _camDepth, _vw, _vh, roadHalfWidth);
        }

        /// <summary>
        /// One module of a vertical barrier strip standing on the road surface: the tyre
        /// wall on the collision limit, or the guardrail out in the run-off. Collected
        /// here and flushed far-to-near by <see cref="FlushRails"/>.
        /// </summary>
        private void EmitBarrier(int sub, float height, float module,
                                 RoadGeometry.Projected p1, RoadGeometry.Projected p2,
                                 float lat1, float lat2, int idx, float fog1, float fog2)
        {
            float edge = _grassExtent;
            float sx1 = p1.x + p1.w * lat1;
            float sx2 = p2.x + p2.w * lat2;
            // Entirely off one side of the screen: nothing to draw.
            if ((sx1 < -edge && sx2 < -edge) || (sx1 > edge + _vw && sx2 > edge + _vw)) return;
            if (_railQuads * 4 + 4 > _railVerts.Length) return;

            // Heights use the vertical projection scale (halfH), exactly like hills.
            float h1 = p1.scale * height * _vh * 0.5f;
            float h2 = p2.scale * height * _vh * 0.5f;
            float u1 = WrapAlong(idx, module), u2 = u1 + RoadGeometry.SegmentLength / module;
            float q1 = Q(p1), q2 = Q(p2);
            byte f1 = (byte)(Mathf.Clamp01(fog1) * 255f), f2 = (byte)(Mathf.Clamp01(fog2) * 255f);

            _railSub[_railQuads] = sub;
            int v = _railQuads * 4;
            _railVerts[v + 0] = ToWorld(sx1, p1.y);
            _railVerts[v + 1] = ToWorld(sx2, p2.y);
            _railVerts[v + 2] = ToWorld(sx2, p2.y - h2);
            _railVerts[v + 3] = ToWorld(sx1, p1.y - h1);
            _railUvq[v + 0] = new Vector3(u1 * q1, 0f, q1);
            _railUvq[v + 1] = new Vector3(u2 * q2, 0f, q2);
            _railUvq[v + 2] = new Vector3(u2 * q2, q2, q2);
            _railUvq[v + 3] = new Vector3(u1 * q1, q1, q1);
            Color32 base1 = _textured[sub] ? new Color32(255, 255, 255, f1) : new Color32(170, 176, 184, f1);
            Color32 base2 = base1; base2.a = f2;
            _railColors[v + 0] = base1;
            _railColors[v + 1] = base2;
            _railColors[v + 2] = base2;
            _railColors[v + 3] = base1;
            _railQuads++;
        }

        private void FlushRails()
        {
            // Far to near: the last collected quad is the furthest. Fence and tyre wall
            // are interleaved in the buffer, and each quad remembers its sub-mesh, so
            // this one reversed walk puts both of them in far-to-near order.
            //
            // The two then part company. The FENCE stays in the road mesh, behind every
            // prop, which is where a guardrail out in the run-off belongs. The TYRE WALL
            // goes to its own mesh so it can be sorted against the sprites instead of
            // losing to them on render queue - see wallSortingOrder. Both are still
            // written in the same far-to-near order, so a near module still covers a far
            // one within each of them.
            _wallVertCount = 0;
            _wallIdxCount = 0;
            for (int r = _railQuads - 1; r >= 0; r--)
            {
                int sub = _railSub[r];
                int src = r * 4;
                if (sub == SubRail)
                {
                    if (_wallVertCount + 4 > _wallVerts.Length
                        || _wallIdxCount + 6 > _wallIdx.Length) continue;
                    int w = _wallVertCount;
                    for (int k = 0; k < 4; k++)
                    {
                        _wallVerts[w + k] = _railVerts[src + k];
                        _wallColors[w + k] = _railColors[src + k];
                        _wallUvq[w + k] = _railUvq[src + k];
                    }
                    _wallIdx[_wallIdxCount++] = w;
                    _wallIdx[_wallIdxCount++] = w + 1;
                    _wallIdx[_wallIdxCount++] = w + 2;
                    _wallIdx[_wallIdxCount++] = w;
                    _wallIdx[_wallIdxCount++] = w + 2;
                    _wallIdx[_wallIdxCount++] = w + 3;
                    _wallVertCount += 4;
                    _quadCount++;
                    continue;
                }

                int[] ix = _idx[sub];
                if (_vertCount + 4 > _verts.Length || _idxCount[sub] + 6 > ix.Length) continue;
                int v = _vertCount;
                for (int k = 0; k < 4; k++)
                {
                    _verts[v + k] = _railVerts[src + k];
                    _colors[v + k] = _railColors[src + k];
                    _uvq[v + k] = _railUvq[src + k];
                }
                WriteQuadIndices(sub, v);
                _vertCount += 4;
                _quadCount++;
            }
        }

        /// <summary>
        /// Texture coordinate along the track for the start of segment <paramref name="idx"/>,
        /// wrapped to a window of whole repeats so it stays small and precise.
        /// </summary>
        private static float WrapAlong(int idx, float tile)
        {
            if (tile <= 1f) return 0f;
            float z = idx * RoadGeometry.SegmentLength;
            float window = tile * 64f;
            return (z - Mathf.Floor(z / window) * window) / tile;
        }

        /// <summary>
        /// Emit one quad. Positions are virtual-screen pixels (y down), UVs in texture
        /// space, q the projective weight of each ROW. Colour alpha carries fog.
        /// </summary>
        private void Quad(int sub,
                          float ax, float ay, float au, float av, float aq,
                          float bx, float by, float bu, float bv, float bq,
                          float cx, float cy, float cu, float cv, float cq,
                          float dx, float dy, float du, float dv, float dq,
                          Color color, float fogNear, float fogFar)
        {
            int[] ix = _idx[sub];
            if (_vertCount + 4 > _verts.Length || _idxCount[sub] + 6 > ix.Length) return;

            int v = _vertCount;
            _verts[v + 0] = ToWorld(ax, ay);
            _verts[v + 1] = ToWorld(bx, by);
            _verts[v + 2] = ToWorld(cx, cy);
            _verts[v + 3] = ToWorld(dx, dy);

            _uvq[v + 0] = new Vector3(au * aq, av * aq, aq);
            _uvq[v + 1] = new Vector3(bu * bq, bv * bq, bq);
            _uvq[v + 2] = new Vector3(cu * cq, cv * cq, cq);
            _uvq[v + 3] = new Vector3(du * dq, dv * dq, dq);

            Color32 near = color; near.a = (byte)(Mathf.Clamp01(fogNear) * 255f);
            Color32 far = color;  far.a = (byte)(Mathf.Clamp01(fogFar) * 255f);
            _colors[v + 0] = near;
            _colors[v + 1] = near;
            _colors[v + 2] = far;
            _colors[v + 3] = far;

            WriteQuadIndices(sub, v);
            _vertCount += 4;
            _quadCount++;
        }

        private void WriteQuadIndices(int sub, int v)
        {
            int[] ix = _idx[sub];
            int i = _idxCount[sub];
            ix[i + 0] = v + 0; ix[i + 1] = v + 1; ix[i + 2] = v + 2;
            ix[i + 3] = v + 0; ix[i + 4] = v + 2; ix[i + 5] = v + 3;
            _idxCount[sub] = i + 6;
        }

        private Vector3 ToWorld(float sx, float sy)
        {
            return new Vector3((sx - _vw * 0.5f) / _ppu, (_vh * 0.5f - sy) / _ppu, 0f);
        }

        // ------------------------------------------------------------ projection API

        /// <summary>
        /// Project the PLAYER car. Lateral position and perspective scale come from the
        /// same projection as everything else, but the VERTICAL reference does not
        /// (plan section 10.1): the car is seated on the road under ITSELF, leans a
        /// fraction of the elevation ahead, and is clamped into a screen band.
        /// </summary>
        public bool TryProjectPlayer(float normalisedX, float distanceAhead,
                                     out Vector3 worldPos, out float scale, out bool clamped)
        {
            worldPos = Vector3.zero;
            scale = 0f;
            clamped = false;
            if (_track == null) return false;

            float vh = virtualHeight;
            float vw = vh * referenceAspect;
            float orthoSize = _camera != null ? _camera.orthographicSize : 5f;
            float ppu = vh / (2f * orthoSize);
            float camDepth = RoadGeometry.CameraDepth(fieldOfView);

            float ySelf  = _track.HeightAt(_cameraZ);
            float yAhead = _track.HeightAt(_cameraZ + distanceAhead);
            float camY   = ySelf + cameraHeight;

            float y = ySelf + (yAhead - ySelf) * Mathf.Clamp01(playerElevationFollow);

            RoadGeometry.Projected p = RoadGeometry.Project(
                normalisedX * roadHalfWidth, y, distanceAhead,
                _cameraX * roadHalfWidth, camY, 0f,
                camDepth, vw, vh, roadHalfWidth);

            if (p.camZ <= camDepth) return false;

            // Where the road surface itself is drawn at this depth, for PlayerSeatOffset.
            RoadGeometry.Projected road = RoadGeometry.Project(
                normalisedX * roadHalfWidth, yAhead, distanceAhead,
                _cameraX * roadHalfWidth, camY, 0f,
                camDepth, vw, vh, roadHalfWidth);

            float top    = Mathf.Min(playerScreenTop, playerScreenBottom) * vh;
            float bottom = Mathf.Max(playerScreenTop, playerScreenBottom) * vh;
            float clampedY = Mathf.Clamp(p.y, top, bottom);
            clamped = !Mathf.Approximately(clampedY, p.y);
            p.y = clampedY;

            worldPos = new Vector3((p.x - vw * 0.5f) / ppu, (vh * 0.5f - p.y) / ppu, 0f);
            PlayerSeatOffset = (road.y - p.y) / ppu;
            scale = p.w / ppu;
            return true;
        }

        /// <summary>
        /// Project a road-space point to world space, using the same projection as the
        /// road mesh. NOT for the player car - see <see cref="TryProjectPlayer"/>.
        /// </summary>
        public bool TryProjectRoadPoint(float normalisedX, float distanceAhead,
                                        out Vector3 worldPos, out float scale)
        {
            worldPos = Vector3.zero;
            scale = 0f;
            if (_track == null) return false;

            float vh = virtualHeight;
            float vw = vh * referenceAspect;
            float orthoSize = _camera != null ? _camera.orthographicSize : 5f;
            float ppu = vh / (2f * orthoSize);
            float camDepth = RoadGeometry.CameraDepth(fieldOfView);

            float z = _cameraZ + distanceAhead;
            float camY = _track.HeightAt(_cameraZ) + cameraHeight;
            float y = _track.HeightAt(z);

            RoadGeometry.Projected p = RoadGeometry.Project(
                normalisedX * roadHalfWidth, y, distanceAhead,
                _cameraX * roadHalfWidth, camY, 0f,
                camDepth, vw, vh, roadHalfWidth);

            if (p.camZ <= camDepth) return false;

            worldPos = new Vector3((p.x - vw * 0.5f) / ppu, (vh * 0.5f - p.y) / ppu, 0f);
            scale = p.w / ppu;
            return true;
        }

        private void UploadMesh()
        {
            _mesh.Clear(false);
            _mesh.subMeshCount = SubCount;
            _mesh.SetVertices(_verts, 0, _vertCount);
            _mesh.SetColors(_colors, 0, _vertCount);
            _mesh.SetUVs(0, _uvq, 0, _vertCount);
            for (int s = 0; s < SubCount; s++)
                _mesh.SetIndices(_idx[s], 0, _idxCount[s], MeshTopology.Triangles, s, false);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 1f));

            if (_wallMesh == null) return;
            _wallMesh.Clear(false);
            _wallMesh.SetVertices(_wallVerts, 0, _wallVertCount);
            _wallMesh.SetColors(_wallColors, 0, _wallVertCount);
            _wallMesh.SetUVs(0, _wallUvq, 0, _wallVertCount);
            _wallMesh.SetIndices(_wallIdx, 0, _wallIdxCount, MeshTopology.Triangles, 0, false);
            _wallMesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 1f));
            if (_wallRenderer != null) _wallRenderer.sortingOrder = wallSortingOrder;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_wallMesh != null) Destroy(_wallMesh);
        }
    }
}
