using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// Owns the runtime <see cref="TrackModel"/> and drives <see cref="RoadRenderer"/>.
    ///
    /// Deliberately thin: it holds the track and pushes camera position into the
    /// renderer. In Phase 3 CarSimulation becomes the thing that sets CameraZ/CameraX;
    /// until then the debug scroll below stands in for a car.
    /// </summary>
    public sealed class TrackRuntime : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;
        [SerializeField] private Material roadMaterial;

        [Header("Track")]
        [Tooltip("Off: the V1 circuit (plan 11). On: the short Phase 2 projection test track.")]
        [SerializeField] private bool useTestTrack = false;

        private TrackModel _track;
        private Camera _camera;

        public TrackModel Track { get { return _track; } }
        public RoadRenderer Renderer { get { return roadRenderer; } }

        private void Awake()
        {
            _camera = Camera.main;
            _track = useTestTrack ? TrackBuilder.BuildTestTrack()
                                  : TrackBuilder.BuildMainTrack();

            if (roadRenderer == null)
            {
                Debug.LogError("[TrackRuntime] roadRenderer is not assigned.");
                enabled = false;
                return;
            }

            roadRenderer.Initialise(_track, roadMaterial);
            // Phase 10: the Alpine panorama. Built here so no scene object is needed;
            // absent art simply leaves the camera's clear colour, as before.
            BackgroundView.Create(roadRenderer, roadMaterial);

            Debug.Log("[TrackRuntime] track built: " + (useTestTrack ? "TEST" : "MAIN") + " "
                      + _track.Count + " segments, length "
                      + _track.Length.ToString("F0") + " units");
        }

        private void Update()
        {
            if (_track == null || roadRenderer == null) return;

            float aspect = _camera != null ? _camera.aspect : 16f / 9f;
            roadRenderer.Rebuild(aspect);
        }
    }
}
