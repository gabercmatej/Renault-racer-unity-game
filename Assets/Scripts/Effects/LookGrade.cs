using System.Collections.Generic;
using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// The game's "post-graded" look, in one place and at near-zero cost.
    ///
    ///  A. A colour grade (exposure, saturation, S-curve contrast, split-toning, black lift)
    ///     applied INSIDE every world-layer shader through RRGrade.cginc, driven by three
    ///     global shader vectors set here. No render pass, no texture. The UI is not touched.
    ///  B. One procedural fullscreen overlay drawn after the world and before the overlay
    ///     canvas: a soft aspect-aware vignette and a faint warm haze band on the horizon.
    ///  C. The distance haze (RoadRenderer) retuned so far road and props melt into the
    ///     graded panorama horizon.
    ///
    /// Everything is driven by <see cref="amount"/>: 0 is an exact identity (the grade is a
    /// lerp from the original colour, the overlay is switched off), 1 is the chosen look.
    /// The component creates itself after the scene loads - no scene edit.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public sealed class LookGrade : MonoBehaviour
    {
        public enum Preset { Off, Subtle, Medium, Strong }

        public static LookGrade Instance { get; private set; }

        /// <summary>Highest quality tier index that still draws the fullscreen overlay.</summary>
        public const int OverlayMaxTier = 1;
        private static int s_tier;
        public static void SetQualityTier(int tier) { s_tier = tier; }

        [Header("Master")]
        [Tooltip("Master switch. Off = the pre-grade look exactly (no overlay, grade amount 0).")]
        public bool lookEnabled = true;
        [Tooltip("0 = identity, 1 = the look below. Scales the grade and the overlay together.")]
        [Range(0f, 1f)] public float amount = 1f;
        public Preset startPreset = Preset.Medium;
        [Tooltip("Scales every deviation-from-neutral below (saturation-1, contrast, tones, lift, " +
                 "exposure-1, vignette, glow, haze pull) at once. 1 = shipped, the approved look (d2c1000); 1.2 = the rejected +20% deviation; " +
                 "0 = identity. Does not touch 'amount', which stays the on/off lerp.")]
        [Range(0f, 1.5f)] public float strength = 1.0f;

        [Header("A. Grade")]
        [Range(0f, 1.5f)] public float exposure = 1.02f;
        [Range(0.5f, 1.6f)] public float saturation = 1.14f;
        [Tooltip("How much of a smoothstep S-curve is mixed in. Ends stay fixed: no clipping.")]
        [Range(0f, 1f)] public float contrast = 0.40f;
        [Tooltip("Added to dark tones (weighted by darkness). Negative red / positive blue = cool shadows.")]
        public Vector3 shadowTone = new Vector3(-0.020f, 0.008f, 0.030f);
        [Tooltip("Added to light tones (weighted by brightness). Positive red = warm highlights.")]
        public Vector3 highlightTone = new Vector3(0.040f, 0.020f, -0.025f);
        [Tooltip("Filmic toe: lifts pure black a touch. Keep tiny.")]
        [Range(0f, 0.05f)] public float blackLift = 0.010f;

        [Header("B. Overlay")]
        [Range(0f, 1f)] public float vignette = 0.42f;
        [Tooltip("Normalised radius where the vignette starts and where it is full (corner ~1.4).")]
        public Vector2 vignetteRange = new Vector2(0.55f, 1.45f);
        [Tooltip("Lower = sides of wide phones are darkened less than 16:9.")]
        [Range(0.5f, 1f)] public float vignetteAspectRelief = 0.78f;
        public Color vignetteColor = new Color(0.020f, 0.035f, 0.060f);
        [Range(0f, 0.5f)] public float glow = 0.16f;
        [Tooltip("Screen height (0 bottom .. 1 top) of the haze band. The road horizon is the middle.")]
        [Range(0.3f, 0.7f)] public float glowHorizon = 0.52f;
        [Range(0.02f, 0.4f)] public float glowWidth = 0.15f;
        public Color glowColor = new Color(1.00f, 0.80f, 0.56f);

        [Header("C. Haze (written to RoadRenderer)")]
        public bool controlHaze = true;
        public Color hazeColor = new Color(0.72f, 0.82f, 0.92f);
        [Tooltip("Haze-to-horizon-colour pull at full amount; blends back to 0.30 (the old look) as amount -> 0.")]
        [Range(0f, 1f)] public float hazeHorizonMatch = 0.55f;
        [Range(0f, 8f)] public float fogDensity = 3.5f;

        private static readonly int G0 = Shader.PropertyToID("_RRGrade0");
        private static readonly int G1 = Shader.PropertyToID("_RRGrade1");
        private static readonly int G2 = Shader.PropertyToID("_RRGrade2");
        private static readonly int PVig = Shader.PropertyToID("_Vig");
        private static readonly int PVigC = Shader.PropertyToID("_VigColor");
        private static readonly int PGlow = Shader.PropertyToID("_Glow");
        private static readonly int PGlowC = Shader.PropertyToID("_GlowColor");

        private MeshRenderer _overlay;
        private Material _overlayMat;
        private Material _spriteBase;
        private readonly Dictionary<Material, Material> _spriteSwap = new Dictionary<Material, Material>();
        private RoadRenderer _road;
        private BackgroundView _bg;
        private Camera _cam;
        private Color _lastHaze = new Color(-1, -1, -1);
        private float _nextScan;
        private int _scans;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
#if UNITY_EDITOR
            // Editor-only: lets the capture tool render the true pre-grade baseline.
            if (System.Environment.GetEnvironmentVariable("RR_NO_LOOK") == "1") return;
#endif
            if (Object.FindAnyObjectByType<RoadRenderer>() == null) return;     // not the race scene
            new GameObject("LookGrade").AddComponent<LookGrade>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            ApplyPreset(startPreset);
        }

        private void Start()
        {
            _road = FindAnyObjectByType<RoadRenderer>();
            _bg = FindAnyObjectByType<BackgroundView>();
            _cam = Camera.main;
            _spriteBase = Resources.Load<Material>("Materials/GradedSprite");
            BuildOverlay();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            // leave the globals at identity so nothing outlives the component
            Shader.SetGlobalVector(G0, new Vector4(0f, 1f, 0f, 1f));
        }

        /// <summary>The three named looks. Off = identity.</summary>
        public void ApplyPreset(Preset p)
        {
            startPreset = p;
            switch (p)
            {
                case Preset.Off:
                    lookEnabled = false;
                    break;
                case Preset.Subtle:
                    lookEnabled = true; amount = 1f;
                    exposure = 1.01f; saturation = 1.08f; contrast = 0.25f;
                    shadowTone = new Vector3(-0.012f, 0.004f, 0.018f);
                    highlightTone = new Vector3(0.025f, 0.012f, -0.015f);
                    blackLift = 0.006f;
                    vignette = 0.28f; glow = 0.10f;
                    hazeHorizonMatch = 0.42f;
                    break;
                case Preset.Medium:
                    lookEnabled = true; amount = 1f;
                    exposure = 1.02f; saturation = 1.14f; contrast = 0.40f;
                    shadowTone = new Vector3(-0.020f, 0.008f, 0.030f);
                    highlightTone = new Vector3(0.040f, 0.020f, -0.025f);
                    blackLift = 0.010f;
                    vignette = 0.42f; glow = 0.16f;
                    hazeHorizonMatch = 0.55f;
                    break;
                case Preset.Strong:
                    lookEnabled = true; amount = 1f;
                    exposure = 1.03f; saturation = 1.22f; contrast = 0.60f;
                    shadowTone = new Vector3(-0.030f, 0.012f, 0.045f);
                    highlightTone = new Vector3(0.060f, 0.030f, -0.040f);
                    blackLift = 0.015f;
                    vignette = 0.60f; glow = 0.24f;
                    hazeHorizonMatch = 0.68f;
                    break;
            }
        }

        private void BuildOverlay()
        {
            Material src = Resources.Load<Material>("Materials/LookOverlay");
            if (src == null) return;
            _overlayMat = new Material(src) { name = "LookOverlay (runtime)" };
            _overlayMat.renderQueue = 4000;
            var mesh = new Mesh { name = "LookOverlayQuad" };
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
            var go = new GameObject("LookOverlayQuad", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            _overlay = go.GetComponent<MeshRenderer>();
            _overlay.sharedMaterial = _overlayMat;
            _overlay.sortingOrder = 32767;
            _overlay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _overlay.receiveShadows = false;
            _overlay.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _overlay.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _overlay.enabled = false;
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime >= _nextScan)
            {
                SwapSpriteShaders();
                _scans++;
                _nextScan = Time.unscaledTime + (_scans < 4 ? 0.2f : 3f);
            }

            float a = lookEnabled ? amount : 0f;
            float k = strength;
            Vector4 g0 = new Vector4(a, 1f + (saturation - 1f) * k, Mathf.Clamp01(contrast * k), 1f + (exposure - 1f) * k);
            Shader.SetGlobalVector(G0, g0);
            Shader.SetGlobalVector(G1, new Vector4(shadowTone.x * k, shadowTone.y * k, shadowTone.z * k, blackLift * k));
            Shader.SetGlobalVector(G2, new Vector4(highlightTone.x * k, highlightTone.y * k, highlightTone.z * k, 0f));

            if (_overlay != null)
            {
                bool on = a > 0f && (vignette > 0f || glow > 0f) && s_tier <= OverlayMaxTier;
                _overlay.enabled = on;
                if (on)
                {
                    _overlayMat.SetVector(PVig, new Vector4(Mathf.Clamp01(vignette * k) * a, vignetteRange.x, vignetteRange.y, vignetteAspectRelief));
                    _overlayMat.SetColor(PVigC, vignetteColor);
                    _overlayMat.SetVector(PGlow, new Vector4(glow * k * a, glowHorizon, glowWidth, 0.5f));
                    _overlayMat.SetColor(PGlowC, glowColor);
                }
            }

            if (_bg != null && _cam != null)
                _cam.backgroundColor = GradeCpu(_bg.fillColor, g0);

            if (controlHaze && _road != null) PushHaze(a);
        }

        /// <summary>The haze match before this look existed. amount 0 puts it back exactly.</summary>
        public const float LegacyHazeMatch = 0.30f;

        private void PushHaze(float a)
        {
            if (_road.hazeColor != hazeColor) _road.hazeColor = hazeColor;
            float match = Mathf.Lerp(LegacyHazeMatch, LegacyHazeMatch + (hazeHorizonMatch - LegacyHazeMatch) * strength, a);
            if (!Mathf.Approximately(_road.hazeHorizonMatch, match)) _road.hazeHorizonMatch = match;
            if (!Mathf.Approximately(_road.fogDensity, fogDensity)) _road.fogDensity = fogDensity;
            Color h = _road.EffectiveHaze;
            if (h == _lastHaze) return;
            _lastHaze = h;
            // Road bands, decals, tyre walls (children of the road renderer)...
            foreach (var r in _road.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.HasProperty("_Haze")) m.SetColor("_Haze", h);
            // ...and the shared roadside / pickup sprite material.
            var pm = Resources.Load<Material>(RoadsideSprites.MaterialPath);
            if (pm != null) pm.SetColor("_Haze", h);
        }

        /// <summary>Built-in Sprites/Default users (car, shadow, particles, burst) -> graded copy.</summary>
        private void SwapSpriteShaders()
        {
            if (_spriteBase == null) return;
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!(r is SpriteRenderer) && !(r is ParticleSystemRenderer)) continue;
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null || m.shader.name != "Sprites/Default") continue;
                Material g;
                if (!_spriteSwap.TryGetValue(m, out g))
                {
                    g = new Material(_spriteBase) { name = m.name + " (graded)" };
                    g.mainTexture = m.mainTexture;
                    if (m.HasProperty("_Color")) g.SetColor("_Color", m.GetColor("_Color"));
                    g.renderQueue = m.renderQueue;
                    _spriteSwap[m] = g;
                }
                r.sharedMaterial = g;
            }
        }

        /// <summary>CPU mirror of RRGrade.cginc, for the camera clear colour.</summary>
        public static Color GradeCpu(Color c0, Vector4 g0)
        {
            var inst = Instance;
            if (inst == null || g0.x <= 0f) return c0;
            Vector3 c = new Vector3(c0.r, c0.g, c0.b) * g0.w;
            float l = c.x * 0.299f + c.y * 0.587f + c.z * 0.114f;
            Vector3 lv = new Vector3(l, l, l);
            c = lv + (c - lv) * g0.y;
            c = new Vector3(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z));
            c = Vector3.Lerp(c, new Vector3(Sm(c.x), Sm(c.y), Sm(c.z)), g0.z);
            l = c.x * 0.299f + c.y * 0.587f + c.z * 0.114f;
            float k = inst.strength;
            c += inst.shadowTone * (k * (1f - l) * (1f - l)) + inst.highlightTone * (k * l * l);
            c += (Vector3.one - c) * (inst.blackLift * k);
            c = new Vector3(Mathf.Clamp01(c.x), Mathf.Clamp01(c.y), Mathf.Clamp01(c.z));
            Vector3 o = new Vector3(c0.r, c0.g, c0.b);
            o += (c - o) * g0.x;
            return new Color(o.x, o.y, o.z, c0.a);
        }

        private static float Sm(float x) { return x * x * (3f - 2f * x); }
    }
}
