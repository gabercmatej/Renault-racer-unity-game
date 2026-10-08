using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// The Alpine panorama behind the road (Phase 10). One quad, one draw call, drawn
    /// before everything else.
    ///
    /// The source is ONE flattened painting, not parallax layers, so it moves as one
    /// restrained plane: sideways with the curve the camera is travelling through, a
    /// fraction of what a true far-distance background would, and not at all vertically
    /// beyond keeping its base tucked under the horizon. Faking layered parallax from a
    /// flat image slides mountains against forest and looks wrong.
    ///
    /// The road's own horizon is fixed at the vertical middle of the screen (the camera
    /// looks level), so the painting's base sits just below it; below the painting the
    /// camera clears to the painting's bottom colour, which is what shows if a crest
    /// hides the far road.
    /// </summary>
    public sealed class BackgroundView : MonoBehaviour
    {
        public const string TexturePath = "Environment/background";

        [Tooltip("Height of the painting on screen, virtual px (screen is 720 high).")]
        public float heightPx = 640f;

        [Tooltip("Screen y of the painting's bottom edge, virtual px from the top. The flat " +
                 "horizon is 360; a little lower hides the base behind the far road.")]
        public float bottomPx = 410f;

        [Tooltip("Screen widths the panorama scrolls per unit of curve per segment travelled. " +
                 "The geometrically correct value - the one at which the mountains sit still " +
                 "in the world while the car rotates past them - is ~0.0029.\n\n" +
                 "It was held at 0.001, about a THIRD of correct, and that turned out to " +
                 "cost more than it saved. A background that barely moves says the world is " +
                 "not rotating, so the only thing telling the player they are turning is the " +
                 "road bending - and the road bends whether they steer or not. Raised to " +
                 "0.0026 (90% of geometric): the far distance now swings as the car changes " +
                 "heading, which is the cheapest honest cue that the CAR is the thing being " +
                 "turned. Held just under the exact value because the painting is one flat " +
                 "plane rather than true parallax layers, and the last 10% is where a flat " +
                 "panorama starts to read as a sliding backdrop.")]
        [Range(0f, 0.004f)] public float curveScroll = 0.0026f;

        [Tooltip("Colour the camera clears to under the painting.")]
        public Color fillColor = new Color(84f / 255f, 120f / 255f, 139f / 255f);

        private RoadRenderer _road;
        private Mesh _mesh;
        private Material _material;
        private Camera _camera;
        // Scroll position IN TEXTURE REPEATS, kept in [0, 1). See LateUpdate for why it is
        // not stored in screen widths any more.
        private float _scrollU;
        private float _lastZ;
        private bool _hasLastZ;
        private readonly Vector3[] _verts = new Vector3[4];
        private readonly Vector3[] _uvq = new Vector3[4];
        private static readonly int[] Indices = { 0, 1, 2, 0, 2, 3 };
        private float _texAspect = 3f;

        public static BackgroundView Create(RoadRenderer road, Material baseMaterial)
        {
            Texture2D tex = Resources.Load<Texture2D>(TexturePath);
            if (tex == null || baseMaterial == null) return null;

            GameObject go = new GameObject("Background");
            BackgroundView view = go.AddComponent<BackgroundView>();
            view._road = road;
            view._texAspect = (float)tex.width / tex.height;
            view._camera = Camera.main;

            view._material = new Material(baseMaterial) { name = "Background" };
            view._material.mainTexture = tex;
            view._material.renderQueue = 1000;              // before the road and everything else
            // U wraps; V CLAMPS. Setting wrapMode alone overrode the import's clamped V, and
            // a repeating V bleeds the sky's top row into the painting's bottom edge.
            tex.wrapModeU = TextureWrapMode.Repeat;
            tex.wrapModeV = TextureWrapMode.Clamp;

            view._mesh = new Mesh { name = "Background" };
            view._mesh.MarkDynamic();
            var colors = new Color32[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
                                         new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            view._mesh.vertices = view._verts;
            view._mesh.colors32 = colors;
            view._mesh.SetUVs(0, view._uvq);
            view._mesh.triangles = Indices;

            go.AddComponent<MeshFilter>().sharedMesh = view._mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = view._material;
            mr.sortingOrder = -100;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            if (view._camera != null) view._camera.backgroundColor = view.fillColor;
            return view;
        }

        private void LateUpdate()
        {
            if (_road == null || _road.Track == null || _mesh == null) return;

            float vh = _road.virtualHeight;
            float orthoSize = _camera != null ? _camera.orthographicSize : 5f;
            float aspect = _camera != null ? _camera.aspect : 16f / 9f;
            float ppu = vh / (2f * orthoSize);
            float halfW = orthoSize * aspect;

            float top = (vh * 0.5f - (bottomPx - heightPx)) / ppu;
            float bottom = (vh * 0.5f - bottomPx) / ppu;

            // One texture repeat is heightPx * aspect virtual px wide.
            float repeatWorld = heightPx * _texAspect / ppu;
            float screenWidths = (2f * halfW) / repeatWorld;

            // Scroll with the curve actually travelled - only when moving, and only as
            // far as the camera moved this frame, so it is frame-rate independent.
            //
            // THE T06-T07 PANORAMA JUMP. The scroll used to be accumulated in SCREEN
            // WIDTHS and wrapped with Repeat(offset, 1024) (and Repeat(offset, 64) on a
            // restart), then multiplied by screenWidths to get U. A wrap is only invisible
            // if it moves U by a whole number of texture repeats, and 1024 screen widths is
            // 1024 * screenWidths = 384 * aspect repeats - a whole number only at a few
            // aspects (3:2, 7:4, 2.1667). At 16:9 it is 682.67, so every wrap slid the whole
            // painting two thirds of a repeat sideways in one frame.
            //
            // And the lap wraps twice exactly there: the scroll is the integral of the
            // curve, i.e. the road's heading, which starts at 0 and comes back to -0.0026
            // at the end of T06 (the T01-T06 headings cancel), so it dips below zero at
            // segment ~1118 and climbs back over it at ~1190 in T07. That is the "sky and
            // mountains glitch between T06 and T07". It depends on the window's aspect
            // (Editor Game view vs browser canvas) and on whether the lap started from a
            // fresh load (a restart kept the old offset, which no longer crosses zero) -
            // which is why it was seen in the browser build and not in the Editor.
            //
            // Now the scroll is kept directly in texture repeats and wrapped by exactly
            // one repeat, which by construction never moves the picture.
            float z = _road.CameraZ;
            if (_hasLastZ)
            {
                float dz = z - _lastZ;
                if (dz > 0f && dz < 5000f)
                    _scrollU += _road.Track.CurveAt(z) * (dz / RoadGeometry.SegmentLength) * curveScroll * screenWidths;
            }
            else _hasLastZ = true;
            _lastZ = z;
            _scrollU = Mathf.Repeat(_scrollU, 1f);

            float u0 = _scrollU - screenWidths * 0.5f;
            float u1 = u0 + screenWidths;

            _verts[0] = new Vector3(-halfW, bottom, 0f);
            _verts[1] = new Vector3(halfW, bottom, 0f);
            _verts[2] = new Vector3(halfW, top, 0f);
            _verts[3] = new Vector3(-halfW, top, 0f);
            _uvq[0] = new Vector3(u0, 0f, 1f);
            _uvq[1] = new Vector3(u1, 0f, 1f);
            _uvq[2] = new Vector3(u1, 1f, 1f);
            _uvq[3] = new Vector3(u0, 1f, 1f);
            _mesh.vertices = _verts;
            _mesh.SetUVs(0, _uvq);
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(10000f, 10000f, 1f));
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }
    }
}
