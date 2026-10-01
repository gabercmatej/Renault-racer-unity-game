using UnityEngine;

namespace RenaultRacer.Core
{
    /// <summary>
    /// Applies the runtime settings from plan section 17.3 / 18 that cannot be set
    /// from the Inspector. Lives on [Systems] in Main.unity and runs once, first.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class Bootstrap : MonoBehaviour
    {
        [SerializeField] private GameConfig config;

        public GameConfig Config { get { return config; } }

        private void Awake()
        {
            int fps = config != null ? config.targetFrameRate : 60;
            Application.targetFrameRate = fps;

            // Unity 2D physics is never used: all collision is analytic in road space.
            // Script mode means Simulate() is only ever called explicitly - and we never call it,
            // which removes the physics step from the frame entirely.
            Physics2D.simulationMode = SimulationMode2D.Script;

            // Bound any residual cost. Gameplay uses its own fixed accumulator.
            Time.fixedDeltaTime = 0.02f;
            Time.maximumDeltaTime = 0.1f;

            // Mobile browsers: never keep the screen awake beyond what the page asks for.
            Screen.sleepTimeout = SleepTimeout.SystemSetting;

            // BEFORE ANYTHING DRAWS. Production art loads through Resources and every
            // loader degrades quietly to a plain generated shape, which is right for a
            // half-built project and a trap for a finished one - the Editor drew a flat
            // red circle where the brake button belongs for a whole session while the web
            // build of the same commit drew the real one, and nothing said so.
            ArtIntegrity.VerifyAtStartup();

            Debug.Log("[Bootstrap] targetFrameRate=" + fps
                      + " physics2D=" + Physics2D.simulationMode
                      + " config=" + (config != null ? config.name : "NONE"));
        }
    }
}
