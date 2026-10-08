using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.Effects
{
    /// <summary>
    /// Tyre smoke and impact sparks (plan items 4.2 and 4.6).
    ///
    /// Both are persistent ParticleSystems with hard `maxParticles` caps. A Unity
    /// ParticleSystem pre-allocates its particle buffer, so it IS a pool: nothing is
    /// instantiated or destroyed during play, which is what plan section 16 requires.
    /// Adding a GameObject pool on top would cost more and buy nothing.
    ///
    /// Emitters are positioned by the road projection, so smoke comes off the tyres
    /// wherever the car actually is rather than from a fixed screen point.
    ///
    /// Budget: 40 smoke + 12 sparks = 52 particles, against the plan's 120 ceiling.
    ///
    /// There is no "going fast" effect here. Phase 10 added a cyan crackle at the rear
    /// to say "electric", and at the speed it triggered it sat on top of the car and
    /// read as a power-up hovering over the roof rather than as pace. Speed is told at
    /// the EDGES of the frame instead, by UI.SpeedLines, where peripheral vision reads
    /// motion and nothing is drawn over the car.
    /// </summary>
    public sealed class FeedbackFX : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;
        [SerializeField] private Material particleMaterial;

        [Header("Tyre smoke")]
        [Tooltip("Drift amount below which no smoke is produced at all, so ordinary " +
                 "cornering stays clean and smoke MEANS something.")]
        [Range(0.05f, 0.8f)] public float smokeDriftThreshold = 0.25f;

        [Tooltip("Peak emission per second per wheel at full drift and full speed.")]
        [Range(5f, 80f)] public float smokeMaxRate = 34f;

        [Tooltip("Particles alive per wheel. Two wheels, so double for the total.")]
        [Range(4, 40)] public int smokeMaxParticles = 20;

        [Tooltip("Rear wheel lateral offset, normalised road units.")]
        [Range(0.05f, 0.5f)] public float wheelOffset = 0.22f;

        [Header("Impact sparks")]
        [Tooltip("Particles per impact burst.")]
        [Range(2, 24)] public int sparkBurst = 12;

        private ParticleSystem _smokeL, _smokeR, _sparks;
        private ParticleSystem.EmissionModule _emL, _emR;
        private Transform _smokeLT, _smokeRT, _sparksT;

        /// <summary>Freeze or release the particle systems (pause): live puffs and sparks hold still.</summary>
        public void SetFrozen(bool frozen)
        {
            FreezePs(_smokeL, frozen); FreezePs(_smokeR, frozen); FreezePs(_sparks, frozen);
        }

        private static void FreezePs(ParticleSystem ps, bool frozen)
        {
            if (ps == null) return;
            if (frozen) { if (ps.isPlaying) ps.Pause(false); }
            else if (ps.isPaused) ps.Play(false);
        }

        /// <summary>
        /// Lower the smoke cap at runtime (QualityDirector, plan 20). Only ever DOWN
        /// from the authored value: this is a budget, and a tier must not be able to
        /// spend more than the scene was built for.
        /// </summary>
        public void SetSmokeCap(int cap)
        {
            smokeMaxParticles = Mathf.Clamp(cap, 4, smokeMaxParticles);
            ApplySmokeCap(_smokeL);
            ApplySmokeCap(_smokeR);
        }

        private void ApplySmokeCap(ParticleSystem ps)
        {
            if (ps == null) return;
            var main = ps.main;
            main.maxParticles = smokeMaxParticles;
        }

        public int AliveParticles
        {
            get
            {
                int n = 0;
                if (_smokeL != null) n += _smokeL.particleCount;
                if (_smokeR != null) n += _smokeR.particleCount;
                if (_sparks != null) n += _sparks.particleCount;
                return n;
            }
        }

        private void Awake()
        {
            Initialise(particleMaterial);
        }

        public void Initialise(Material particleMaterial)
        {
            // Phase 10: the effects atlas, if present. One material for every system, so
            // they can batch; each system picks its sprite through texture-sheet UVs.
            FxArt.Load();
            if (FxArt.Texture != null && particleMaterial != null)
            {
                particleMaterial = new Material(particleMaterial) { name = "FX (atlas)" };
                particleMaterial.mainTexture = FxArt.Texture;
                _atlas = true;
            }

            _smokeL = BuildSmoke("SmokeL", particleMaterial);
            _smokeR = BuildSmoke("SmokeR", particleMaterial);
            _sparks = BuildSparks("Sparks", particleMaterial);

            _emL = _smokeL.emission;
            _emR = _smokeR.emission;
            _smokeLT = _smokeL.transform;
            _smokeRT = _smokeR.transform;
            _sparksT = _sparks.transform;
        }

        private bool _atlas;

        private static void UseSprite(ParticleSystem ps, Sprite sprite)
        {
            if (sprite == null) return;
            var tsa = ps.textureSheetAnimation;
            tsa.enabled = true;
            tsa.mode = ParticleSystemAnimationMode.Sprites;
            tsa.AddSprite(sprite);
        }

        private ParticleSystem BuildSmoke(string name, Material mat)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop();

            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = 0.55f;
            main.startSpeed = 0.7f;
            main.startSize = 0.55f;
            main.startColor = new Color(0.92f, 0.92f, 0.92f, 0.5f);
            if (_atlas) main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.maxParticles = smokeMaxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.04f;              // drifts gently upward
            main.playOnAwake = false;

            var em = ps.emission;
            em.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.12f;

            // Grow and fade. Cheap, and it keeps overdraw low because the big
            // particles are also the most transparent ones.
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            AnimationCurve grow = new AnimationCurve();
            grow.AddKey(0f, 0.45f);
            grow.AddKey(1f, 1.5f);
            sol.size = new ParticleSystem.MinMaxCurve(1f, grow);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 1f, 1f), 0f),
                    new GradientColorKey(new Color(0.82f, 0.82f, 0.84f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.55f, 0.18f),
                    new GradientAlphaKey(0f, 1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(g);

            // Everything we do not need, explicitly off.
            var noise = ps.noise; noise.enabled = false;
            var trails = ps.trails; trails.enabled = false;
            var lights = ps.lights; lights.enabled = false;
            var collision = ps.collision; collision.enabled = false;
            var sub = ps.subEmitters; sub.enabled = false;
            if (_atlas) UseSprite(ps, FxArt.Smoke);

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.sortingOrder = 50;                        // behind the car sprite (100)
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            ps.Play();
            return ps;
        }

        private ParticleSystem BuildSparks(string name, Material mat)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop();

            var main = ps.main;
            main.duration = 1f;
            main.loop = false;
            main.startLifetime = 0.28f;
            main.startSpeed = 4.5f;
            main.startSize = 0.16f;
            main.startColor = new Color(1f, 0.85f, 0.35f, 1f);
            main.maxParticles = 24;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1.2f;
            main.playOnAwake = false;

            var em = ps.emission;
            em.enabled = false;                         // burst-only, via Emit()

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.05f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 0.95f, 0.6f), 0f),
                    new GradientColorKey(new Color(1f, 0.45f, 0.1f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(g);

            var noise = ps.noise; noise.enabled = false;
            var trails = ps.trails; trails.enabled = false;
            var lights = ps.lights; lights.enabled = false;
            var collision = ps.collision; collision.enabled = false;
            if (_atlas)
            {
                UseSprite(ps, FxArt.Impact);
                main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            }

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sortingOrder = 150;                       // in front of the car
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            return ps;
        }

        /// <summary>
        /// Drive the smoke from the car's actual slip state. Called once per frame.
        /// </summary>
        public void UpdateSmoke(float carX, float driftAmount, float lateralSlip,
                               float speedPct, float distanceAhead)
        {
            if (_smokeL == null || roadRenderer == null) return;

            // Gate on real slip, so ordinary cornering produces nothing and smoke
            // remains a genuine signal that the rear has let go.
            float slip = Mathf.Max(driftAmount, lateralSlip);
            float rate = 0f;
            if (slip > smokeDriftThreshold && speedPct > 0.1f)
            {
                float t = Mathf.InverseLerp(smokeDriftThreshold, 1f, slip);
                rate = smokeMaxRate * t * Mathf.Clamp01(speedPct * 1.2f);
            }
            _emL.rateOverTime = rate;
            _emR.rateOverTime = rate;

            if (rate <= 0f) return;   // no need to reposition if nothing is emitting

            Vector3 pos;
            float scale;
            if (roadRenderer.TryProjectRoadPoint(carX - wheelOffset, distanceAhead, out pos, out scale))
                _smokeLT.position = pos;
            if (roadRenderer.TryProjectRoadPoint(carX + wheelOffset, distanceAhead, out pos, out scale))
                _smokeRT.position = pos;
        }

        /// <summary>
        /// Smoke from the drawn rear tyres (world positions supplied by CarView), so it
        /// stays on the car whichever perspective view is shown and whatever the hill.
        /// </summary>
        public void UpdateSmokeAt(Vector3 leftWheel, Vector3 rightWheel, float driftAmount,
                                  float lateralSlip, float speedPct)
        {
            if (_smokeL == null) return;
            float rate = SmokeRate(driftAmount, lateralSlip, speedPct);
            _emL.rateOverTime = rate;
            _emR.rateOverTime = rate;
            if (rate <= 0f) return;
            _smokeLT.position = leftWheel;
            _smokeRT.position = rightWheel;
        }

        private float SmokeRate(float driftAmount, float lateralSlip, float speedPct)
        {
            float slip = Mathf.Max(driftAmount, lateralSlip);
            if (slip <= smokeDriftThreshold || speedPct <= 0.1f) return 0f;
            float t = Mathf.InverseLerp(smokeDriftThreshold, 1f, slip);
            return smokeMaxRate * t * Mathf.Clamp01(speedPct * 1.2f);
        }

        /// <summary>Impact burst at a world position (the drawn car).</summary>
        public void ImpactAt(float strength, Vector3 worldPos)
        {
            if (_sparks == null) return;
            _sparksT.position = worldPos;
            int n = Mathf.Max(2, Mathf.RoundToInt(sparkBurst * Mathf.Clamp01(strength)));
            _sparks.Emit(n);
        }

        /// <summary>
        /// A lightning pickup taken: one short cyan-white burst at the car, on the spark
        /// system that already exists (no new emitter, no allocation - EmitParams is a
        /// struct). A single pop, not a lingering effect: a persistent glow over the car
        /// read as a power-up hovering on the roof (see the class notes).
        /// </summary>
        public void BoostBurstAt(Vector3 worldPos)
        {
            if (_sparks == null) return;
            var ep = new ParticleSystem.EmitParams();
            ep.position = worldPos;
            ep.applyShapeToPosition = true;
            ep.startColor = new Color32(170, 240, 255, 255);
            _sparks.Emit(ep, Mathf.Max(6, sparkBurst));
        }

        /// <summary>One short burst on barrier contact. Strength is 0..1.</summary>
        public void Impact(float strength, float carX, float distanceAhead)
        {
            if (_sparks == null || roadRenderer == null) return;

            Vector3 pos;
            float scale;
            if (roadRenderer.TryProjectRoadPoint(carX, distanceAhead, out pos, out scale))
                _sparksT.position = pos;

            int n = Mathf.Max(2, Mathf.RoundToInt(sparkBurst * Mathf.Clamp01(strength)));
            _sparks.Emit(n);
        }

        /// <summary>Stop everything and clear live particles. Used on race restart.</summary>
        public void ClearAll()
        {
            if (_smokeL != null) { _emL.rateOverTime = 0f; _smokeL.Clear(); }
            if (_smokeR != null) { _emR.rateOverTime = 0f; _smokeR.Clear(); }
            if (_sparks != null) _sparks.Clear();
        }
    }
}
