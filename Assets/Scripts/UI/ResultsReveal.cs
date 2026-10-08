namespace RenaultRacer.UI
{
    /// <summary>
    /// The finish card's reveal as GameDirector sees it - one question, asked on the tap
    /// that would otherwise restart: "was this tap yours?"
    ///
    /// A tap while the card is still revealing completes the reveal instantly and is
    /// consumed (returns true), so the player never loses the numbers to an impatient tap.
    /// A tap on the play-again button, or any tap once the reveal has finished, is not
    /// consumed (returns false) and the director restarts. With no finish view registered
    /// (the invalid-lap panel, tests) every tap restarts, exactly as before.
    ///
    /// Static so the director - which lives below the UI - needs no reference to it.
    /// </summary>
    public static class ResultsReveal
    {
        /// <summary>The finish view, registered while it exists.</summary>
        public static IRevealSkippable Current;

        public static bool ConsumeTapAsSkip()
        {
            return Current != null && Current.ConsumeTapAsSkip();
        }
    }

    public interface IRevealSkippable
    {
        /// <summary>Called on a fresh tap in the Finished state. True = the tap completed the reveal.</summary>
        bool ConsumeTapAsSkip();
    }
}
