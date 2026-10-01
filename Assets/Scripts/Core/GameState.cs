namespace RenaultRacer.Core
{
    /// <summary>
    /// The whole flow (plan section 7). A plain enum with one SetState switch in
    /// GameDirector - no state-machine framework, because there are nine states and
    /// they change about once a minute.
    ///
    /// Phase 5 implements Boot -> Ready -> Countdown -> Racing -> Finished/Invalid.
    /// RotateGate arrives with the mobile controls in Phase 6; Submitting, Confirmed
    /// and SubmitError arrive with the backend in Phase 12. They are declared here so
    /// those phases slot in without reshaping the flow.
    /// </summary>
    public enum GameState
    {
        Boot = 0,
        Ready = 1,
        Countdown = 2,
        Racing = 3,
        Finished = 4,

        /// <summary>Lap rejected by RaceValidator - never submitted (plan section 13).</summary>
        Invalid = 5,

        RotateGate = 6,     // Phase 6
        Submitting = 7,     // Phase 12
        Confirmed = 8,      // Phase 12
        SubmitError = 9     // Phase 12
    }
}
