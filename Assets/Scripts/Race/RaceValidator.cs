using RenaultRacer.Core;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.Race
{
    /// <summary>
    /// Decides whether a finished lap is real (plan section 13).
    ///
    /// Proportionate to a browser campaign game: make accidental and trivial
    /// exploits impossible, and do not pretend to do more. The build is client-side
    /// WASM; a determined user always wins, and real protection is server-side.
    /// Deliberately NOT doing: obfuscation, memory-integrity checks, encrypted
    /// payloads, server-replayed physics.
    /// </summary>
    public enum LapRejection
    {
        None = 0,
        MissedCheckpoints,
        ImpossiblyFast,
        ImplausiblySlow,
        NoCountdownStart,
        NotFinished,
        Teleported
    }

    public static class RaceValidator
    {
        /// <summary>
        /// The fastest lap physically possible: the car can never exceed maxSpeed, so
        /// no lap can beat trackLength / maxSpeed. Deriving the floor beats guessing
        /// an "ideal lap" - it cannot drift out of date when the track is retuned.
        /// </summary>
        public static float PhysicalFloorSeconds(TrackModel track, HandlingProfile profile)
        {
            if (track == null || profile == null || profile.maxSpeed <= 0f) return 0f;
            return track.Length / profile.maxSpeed;
        }

        public static LapRejection Validate(RaceSession session, TrackModel track,
                                            HandlingProfile profile, GameConfig config)
        {
            return Validate(session, track, profile, config, 0f);
        }

        /// <param name="boostSecondsSaved">The most lap time lightning pickups can save
        /// against the floor (BoostTuning.MaxSecondsSaved): a boosted car may exceed
        /// maxSpeed, so the floor is lowered by exactly that provable bound.</param>
        public static LapRejection Validate(RaceSession session, TrackModel track,
                                            HandlingProfile profile, GameConfig config,
                                            float boostSecondsSaved)
        {
            if (session == null || track == null || profile == null) return LapRejection.NotFinished;

            // 4. A finish cannot exist without a matching start.
            if (!session.StartedFromCountdown) return LapRejection.NoCountdownStart;

            if (!session.Finished) return LapRejection.NotFinished;

            // 1b. Position must have advanced continuously for the whole lap.
            if (session.ContinuityBroken) return LapRejection.Teleported;

            // 1. Every checkpoint, in order. Blocks skipping part of the lap.
            if (session.CheckpointsPassed < session.CheckpointCount)
                return LapRejection.MissedCheckpoints;

            // 2. Below the physical floor means the clock or the sim was tampered with.
            float tolerance = config != null ? config.minLapTimeFraction : 0.98f;
            float floor = System.Math.Max(0f, PhysicalFloorSeconds(track, profile) - System.Math.Max(0f, boostSecondsSaved))
                        * tolerance;
            if (session.ElapsedSeconds < floor) return LapRejection.ImpossiblyFast;

            // 3. Sanity bound - rejects an abandoned tab rather than a bad driver.
            float ceiling = config != null ? config.maxLapTimeSeconds : 900f;
            if (session.ElapsedSeconds > ceiling) return LapRejection.ImplausiblySlow;

            return LapRejection.None;
        }

        /// <summary>
        /// Neutral player-facing reason. Never blames the player for a glitch.
        /// DRAFT COPY pending M7 - the client supplies or approves the final
        /// Slovenian wording in Phase 7. Diacritics are omitted here only because
        /// this text is currently shown by the debug HUD.
        /// </summary>
        public static string Describe(LapRejection r)
        {
            switch (r)
            {
                case LapRejection.MissedCheckpoints: return "Krog ni bil dokoncan pravilno.";
                case LapRejection.ImpossiblyFast:    return "Cas kroga ni veljaven.";
                case LapRejection.ImplausiblySlow:   return "Dirka je trajala predolgo.";
                case LapRejection.NoCountdownStart:  return "Dirka se ni zacela pravilno.";
                case LapRejection.NotFinished:       return "Krog ni bil dokoncan.";
                case LapRejection.Teleported:        return "Krog ni bil dokoncan pravilno.";
                default:                             return "";
            }
        }
    }
}
