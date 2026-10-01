using UnityEngine;

namespace RenaultRacer.Core
{
    /// <summary>
    /// Project-wide configuration that is not gameplay tuning.
    /// Handling values live in HandlingProfile; track layout lives in TrackModel.
    /// This holds timings, budgets and endpoints only.
    /// </summary>
    [CreateAssetMenu(fileName = "GameConfig", menuName = "Renault Racer/Game Config", order = 0)]
    public sealed class GameConfig : ScriptableObject
    {
        [Header("Frame rate")]
        [Tooltip("Web ignores VSync; requestAnimationFrame drives the loop. Applied at boot.")]
        public int targetFrameRate = 60;

        [Tooltip("Fixed simulation step. The car sim runs on its own accumulator, not FixedUpdate.")]
        public float simulationStep = 1f / 60f;

        [Tooltip("Spiral-of-death guard: max simulation substeps per rendered frame.")]
        public int maxSimulationSteps = 4;

        [Header("Race")]
        [Tooltip("Seconds per countdown lamp before the green.")]
        public float countdownLampInterval = 1f;

        [Tooltip("Number of invisible validation checkpoints spread around the lap.")]
        public int checkpointCount = 4;

        [Tooltip("Reject any lap faster than this fraction of a perfect flat-out lap.")]
        [Range(0.5f, 1f)] public float minLapTimeFraction = 0.85f;

        [Tooltip("Sanity upper bound for a lap, in seconds. Rejects an abandoned tab.")]
        public float maxLapTimeSeconds = 900f;

        [Header("Performance budgets")]
        [Tooltip("Hard cap on simultaneously visible roadside sprites.")]
        public int maxVisibleRoadsideSprites = 80;

        [Tooltip("Segments drawn ahead of the camera.")]
        public int drawDistanceSegments = 300;

        [Header("Backend (wired in Phase 12 - not used before then)")]
        [Tooltip("Origin allowed to postMessage into the game. Never use \"*\".")]
        public string parentOrigin = "";

        [Tooltip("Attempts before showing the submit-error screen.")]
        public int submitMaxAttempts = 3;

        [Tooltip("Per-attempt network timeout, seconds.")]
        public float submitTimeoutSeconds = 15f;
    }
}
