using RenaultRacer.Race;

namespace RenaultRacer.UI
{
    /// <summary>
    /// Every word the player reads, in one file (plan section 14).
    ///
    /// ⚠️ **Not final.** The screen copy below is the user's PROPOSED Slovenian copy from
    /// the Phase 10 brief (assets-notes.txt: START / FINISH / INVALID / ROTATE / LOADING
    /// sections). It is still subject to client sign-off (manual task M7). Lines marked
    /// DRAFT are Claude's wording where the brief proposes nothing - chiefly the
    /// reason-specific invalid-lap lines, which the brief explicitly asks for but does not
    /// write - and need the same review.
    ///
    /// Kept in one file so approving or rewriting the copy is one review of one short
    /// file, not a hunt through the UI code. The loading screen's copy lives in
    /// Assets/WebGLTemplates/ShellRacer/index.html because it is shown before Unity runs.
    ///
    /// Typography: the UI font (RR Montserrat, see prepare_ui.py) is subset to printable
    /// ASCII plus Č č Š š Ž ž Ć ć Đ đ and a few marks (… – — „ “ ” ‘ ’ · × ° « »). Any new
    /// character outside that set must be added to the subset, or it will not render.
    /// </summary>
    public static class UiCopy
    {
        // ---------------------------------------------------------------- Start
        /// <summary>Title: RENAULT in white, RACER in yellow (rich text).</summary>
        public const string TitleLead = "RENAULT";
        public const string TitleAccent = "RACER";

        /// <summary>Proposed copy (brief, START SCREEN).</summary>
        public const string Tagline = "En krog. Tvoj najboljši čas.";

        /// <summary>Proposed copy. The whole screen starts the race; the button is the cue.</summary>
        public const string Start = "ZAČNI";

        /// <summary>DRAFT captions under the control icons on the start screen.</summary>
        public const string HintSteer = "ZAVIJANJE";
        public const string HintBrake = "ZAVORA";

        /// <summary>DRAFT desktop line, shown when there is no touch screen.</summary>
        public const string HintKeys = "PUŠČICE ALI A / D = ZAVIJANJE   ·   DOL, S ALI PRESLEDEK = ZAVORA";

        // ------------------------------------------------------------ Countdown
        /// <summary>
        /// Text fallback for the GO artwork, used only when the UI atlas is missing. Note
        /// the supplied artwork itself reads "GO!" in English.
        /// </summary>
        public const string Go = "GO!";

        // ---------------------------------------------------------------- Race
        /// <summary>Speed unit.</summary>
        public const string SpeedUnit = "km/h";

        // -------------------------------------------------------------- Finish
        /// <summary>Proposed copy (brief, FINISH SCREEN). No submission/prize claims.</summary>
        public const string FinishTitle = "CILJ!";
        public const string YourTime = "TVOJ ČAS";
        public const string PlayAgain = "IGRAJ ZNOVA";

        // ------------------------------------------------------------- Invalid
        /// <summary>Proposed copy (brief, INVALID LAP SCREEN).</summary>
        public const string InvalidTitle = "NEVELJAVEN KROG";
        public const string TryAgain = "POSKUSI ZNOVA";

        /// <summary>
        /// Proposed generic line. The brief's own caveat applies: it is only true when the
        /// player really did not drive the whole lap, so it is used for MissedCheckpoints
        /// (and as the fallback), never for a rejection with a different cause.
        /// </summary>
        public const string InvalidMissedCheckpoints = "Poskusi znova in odpelji celoten krog.";

        /// <summary>DRAFT. Position jumped further than the car can drive - not the player's doing.</summary>
        public const string InvalidTeleported = "Vožnja ni potekala neprekinjeno.";

        /// <summary>DRAFT. Below the physical floor - a glitch, never an accusation.</summary>
        public const string InvalidTooFast = "Časa kroga ni bilo mogoče potrditi.";

        /// <summary>DRAFT. Over the sanity ceiling - an abandoned tab, not a slow driver.</summary>
        public const string InvalidTooSlow = "Krog je trajal predolgo.";

        /// <summary>DRAFT. The finish had no matching countdown start.</summary>
        public const string InvalidNoStart = "Dirka se ni začela z odštevanjem.";

        /// <summary>DRAFT. The session reports the lap as not finished.</summary>
        public const string InvalidNotFinished = "Krog ni bil dokončan.";

        /// <summary>
        /// The body line for a rejection. Returns a constant - no allocation - and maps
        /// every <see cref="LapRejection"/> to a line that is true for THAT reason.
        /// </summary>
        public static string InvalidReason(LapRejection reason)
        {
            switch (reason)
            {
                case LapRejection.MissedCheckpoints: return InvalidMissedCheckpoints;
                case LapRejection.Teleported:        return InvalidTeleported;
                case LapRejection.ImpossiblyFast:    return InvalidTooFast;
                case LapRejection.ImplausiblySlow:   return InvalidTooSlow;
                case LapRejection.NoCountdownStart:  return InvalidNoStart;
                case LapRejection.NotFinished:       return InvalidNotFinished;
                default:                             return InvalidMissedCheckpoints;
            }
        }

        // --------------------------------------------------------- Rotate gate
        /// <summary>Proposed copy (brief, ROTATE PHONE SCREEN).</summary>
        public const string RotatePhone = "ZASUKAJ TELEFON";
        public const string RotateBody = "Za igranje obrni telefon vodoravno.";
    }
}
