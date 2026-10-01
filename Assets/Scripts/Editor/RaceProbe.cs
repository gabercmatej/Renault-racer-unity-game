using System.Text;
using UnityEngine;
using RenaultRacer.Core;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Scripted verification of the race rules (plan sections 12 and 13). Editor-only.
    ///
    /// RaceSession and RaceValidator are pure classes for the same reason
    /// CarSimulation is: the rules can be checked deterministically, including the
    /// cases that are awkward to reproduce by hand - skipping checkpoints, an
    /// impossibly fast time, a finish with no countdown.
    /// </summary>
    public static class RaceProbe
    {
        private const float Step = 1f / 60f;

        public static string RunAll(HandlingProfile profile, GameConfig config)
        {
            TrackModel track = TrackBuilder.BuildMainTrack();
            StringBuilder sb = new StringBuilder(4096);

            sb.AppendLine("================= RACE SYSTEM VERIFICATION =================");
            sb.AppendLine("track " + track.Count + " segments, " + track.Length.ToString("F0")
                          + " units   floor "
                          + RaceValidator.PhysicalFloorSeconds(track, profile).ToString("F1") + "s");
            sb.AppendLine();

            TestCheckpointsExist(track, sb);
            TestTrackCorridorIsSane(track, sb);
            TestHonestLap(track, profile, config, sb);
            TestSkippedCheckpointsRejected(track, profile, config, sb);
            TestTeleportRejected(track, profile, config, sb);
            TestImpossibleTimeRejected(track, profile, config, sb);
            TestNoCountdownRejected(track, profile, config, sb);
            TestUnfinishedRejected(track, profile, config, sb);
            TestClockIsFrameRateIndependent(track, profile, sb);
            TestRestartIsClean(track, profile, sb);

            sb.AppendLine("===========================================================");
            return sb.ToString();
        }

        /// <summary>
        /// Every segment's barrier corridor must contain the road surface, and must not
        /// step sharply from one segment to the next.
        ///
        /// This exists because of a real bug (M5). The chicane's run-off was authored
        /// as SetBarriers(..., 2.0f, 2.0f) - a missing minus sign - which produced a
        /// ZERO-WIDTH corridor two road half-widths right of the centre line. The
        /// collision block does the only thing it can with that: it clamps X onto the
        /// limit. So every lap, on reaching the chicane, the car was teleported roughly
        /// two half-widths sideways in a single 1/60 s step and dumped against the wall.
        /// It reads in play as the car jumping and control being lost, and no amount of
        /// looking at the handling model would have found it.
        /// </summary>
        private static void TestTrackCorridorIsSane(TrackModel track, StringBuilder sb)
        {
            int notContainingRoad = 0, inverted = 0, firstBad = -1;
            float narrowest = float.MaxValue; int narrowestAt = -1;
            float biggestStep = 0f; int biggestStepAt = -1;

            for (int i = 0; i < track.Count; i++)
            {
                Segment a = track[i];
                Segment b = track[i + 1];

                if (a.barrierRight <= a.barrierLeft)
                {
                    inverted++;
                    if (firstBad < 0) firstBad = i;
                }
                if (a.barrierLeft > -1f || a.barrierRight < 1f)
                {
                    notContainingRoad++;
                    if (firstBad < 0) firstBad = i;
                }

                float width = a.barrierRight - a.barrierLeft;
                if (width < narrowest) { narrowest = width; narrowestAt = i; }

                float stepChange = Mathf.Max(Mathf.Abs(b.barrierLeft - a.barrierLeft),
                                             Mathf.Abs(b.barrierRight - a.barrierRight));
                if (stepChange > biggestStep) { biggestStep = stepChange; biggestStepAt = i; }
            }

            sb.AppendLine("[1b] BARRIER CORRIDOR CONTAINS THE ROAD");
            sb.AppendLine("    narrowest corridor " + narrowest.ToString("F2")
                          + " at segment " + narrowestAt
                          + "   largest step change " + biggestStep.ToString("F2")
                          + " at segment " + biggestStepAt);
            sb.AppendLine("    segments not containing the road surface: " + notContainingRoad
                          + "   inverted: " + inverted
                          + (firstBad >= 0 ? "   first at " + firstBad : ""));
            sb.AppendLine("    road always inside the barriers=" + Pass(notContainingRoad == 0)
                          + "  no inverted corridor=" + Pass(inverted == 0)
                          + "  no cliff-edge step (<2.0)=" + Pass(biggestStep < 2f));
            sb.AppendLine();
        }

        private static void TestCheckpointsExist(TrackModel track, StringBuilder sb)
        {
            var s = new RaceSession(track, 0f);
            int gates = 0;
            for (int i = 0; i < track.Count; i++) if (track[i].isFinishGate) gates++;

            sb.AppendLine("[1] TRACK HAS GATES");
            sb.AppendLine("    checkpoints " + s.CheckpointCount + "   finish gates " + gates);
            sb.AppendLine("    4 checkpoints=" + Pass(s.CheckpointCount == 4)
                          + "  exactly one finish gate=" + Pass(gates == 1));
            sb.AppendLine();
        }

        /// <summary>Drive a real lap and require it to be accepted.</summary>
        private static void TestHonestLap(TrackModel track, HandlingProfile p,
                                          GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            var sim = new CarSimulation(track, p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();

            float guard = 0f;
            while (!session.Finished && guard < 180f)
            {
                float ahead = 0f;
                for (int i = 1; i <= 6; i++) ahead += track.CurveAt(sim.Z + 2600f * i / 6f);
                ahead /= 6f;
                inp.Steer = HandlingProbe.HoldLine(sim, Mathf.Clamp(-ahead * 0.11f, -0.75f, 0.75f));
                sim.Step(Step, inp);
                session.Tick(Step, sim.Z);
                guard += Step;
            }

            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            sb.AppendLine("[2] AN HONEST LAP IS ACCEPTED");
            sb.AppendLine("    time " + RaceSession.FormatTime(session.ElapsedSeconds)
                          + "   checkpoints " + session.CheckpointsPassed + "/"
                          + session.CheckpointCount + "   verdict " + r);
            sb.AppendLine("    finished=" + Pass(session.Finished)
                          + "  all checkpoints=" + Pass(session.CheckpointsPassed == session.CheckpointCount)
                          + "  accepted=" + Pass(r == LapRejection.None));
            sb.AppendLine();
        }

        /// <summary>
        /// Jump straight to the line without driving the lap. This is exactly what the
        /// checkpoints exist to catch.
        /// </summary>
        private static void TestSkippedCheckpointsRejected(TrackModel track, HandlingProfile p,
                                                           GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            session.Tick(45f, track.Length + 1f);

            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            sb.AppendLine("[3] SKIPPING THE LAP IS REJECTED");
            sb.AppendLine("    checkpoints " + session.CheckpointsPassed + "/" + session.CheckpointCount
                          + "   finished " + session.Finished + "   verdict " + r);
            sb.AppendLine("    rejected=" + Pass(r == LapRejection.MissedCheckpoints));
            sb.AppendLine();
        }

        /// <summary>
        /// Advance the lap in legal-looking stages, but cover ground faster in each
        /// tick than the car can physically travel.
        /// </summary>
        private static void TestTeleportRejected(TrackModel track, HandlingProfile p,
                                                 GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            // Walk past every checkpoint in turn - so the checkpoint test alone would
            // be satisfied - but jump there far too quickly.
            int steps = 60;
            for (int i = 1; i <= steps; i++)
                session.Tick(Step, track.Length * i / (float)steps);

            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            sb.AppendLine("[3b] TELEPORTING THROUGH THE LAP IS REJECTED");
            sb.AppendLine("    checkpoints " + session.CheckpointsPassed + "/" + session.CheckpointCount
                          + "   continuity broken " + session.ContinuityBroken
                          + "   verdict " + r);
            sb.AppendLine("    rejected=" + Pass(r == LapRejection.Teleported));
            sb.AppendLine();
        }

        /// <summary>
        /// Pass every checkpoint honestly, but in far less time than the car could
        /// physically manage.
        /// </summary>
        private static void TestImpossibleTimeRejected(TrackModel track, HandlingProfile p,
                                                       GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            // Enough ticks that each individual advance stays physically legal - we
            // want this case to isolate the CLOCK check, not trip the continuity one.
            float floor = RaceValidator.PhysicalFloorSeconds(track, p);
            int steps = 4000;
            for (int i = 1; i <= steps; i++)
                session.Tick(floor * 0.5f / steps, track.Length * i / (float)steps);

            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            sb.AppendLine("[4] AN IMPOSSIBLE TIME IS REJECTED");
            sb.AppendLine("    time " + session.ElapsedSeconds.ToString("F1") + "s   floor "
                          + floor.ToString("F1") + "s   checkpoints "
                          + session.CheckpointsPassed + "/" + session.CheckpointCount
                          + "   verdict " + r);
            sb.AppendLine("    all checkpoints hit=" + Pass(session.CheckpointsPassed == session.CheckpointCount)
                          + "  rejected=" + Pass(r == LapRejection.ImpossiblyFast));
            sb.AppendLine();
        }

        private static void TestNoCountdownRejected(TrackModel track, HandlingProfile p,
                                                    GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();          // armed, but StartRacing() never called
            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            sb.AppendLine("[5] A FINISH WITHOUT A START IS REJECTED");
            sb.AppendLine("    verdict " + r + "   rejected=" + Pass(r == LapRejection.NoCountdownStart));
            sb.AppendLine();
        }

        private static void TestUnfinishedRejected(TrackModel track, HandlingProfile p,
                                                   GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();
            session.Tick(10f, track.Length * 0.5f);   // half a lap
            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            sb.AppendLine("[6] HALF A LAP IS NOT A FINISH");
            sb.AppendLine("    progress " + (session.LapProgress * 100f).ToString("F0")
                          + "%   verdict " + r
                          + "   rejected=" + Pass(r == LapRejection.NotFinished));
            sb.AppendLine();
        }

        /// <summary>
        /// The clock accumulates whole simulation steps, so a 30 FPS player and a
        /// 60 FPS player must be timed identically (plan section 12).
        /// </summary>
        private static void TestClockIsFrameRateIndependent(TrackModel track, HandlingProfile p,
                                                            StringBuilder sb)
        {
            float t60 = TimedRun(track, p, 1);
            float t30 = TimedRun(track, p, 2);   // two fixed steps per rendered frame
            float delta = Mathf.Abs(t60 - t30);

            sb.AppendLine("[7] LAP CLOCK IS FRAME-RATE INDEPENDENT");
            sb.AppendLine("    60 FPS " + t60.ToString("F3") + "s   30 FPS " + t30.ToString("F3")
                          + "s   delta " + (delta * 1000f).ToString("F1") + " ms");
            sb.AppendLine("    within one step=" + Pass(delta <= Step + 0.0005f));
            sb.AppendLine();
        }

        private static float TimedRun(TrackModel track, HandlingProfile p, int stepsPerFrame)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();
            var sim = new CarSimulation(track, p);
            sim.Reset(0f);
            var inp = new HandlingProbe.ScriptedInput();

            float guard = 0f;
            while (!session.Finished && guard < 180f)
            {
                for (int i = 0; i < stepsPerFrame; i++) sim.Step(Step, inp);
                session.Tick(Step * stepsPerFrame, sim.Z);
                guard += Step * stepsPerFrame;
            }
            return session.ElapsedSeconds;
        }

        private static void TestRestartIsClean(TrackModel track, HandlingProfile p,
                                               StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();
            session.Tick(12f, track.Length * 0.6f);
            string firstId = session.SessionId;
            int cpBefore = session.CheckpointsPassed;

            session.Begin();   // restart, exactly as GameDirector does

            bool cleared = session.ElapsedSeconds == 0f
                        && session.CheckpointsPassed == 0
                        && !session.Finished
                        && session.LapProgress == 0f
                        && !session.StartedFromCountdown;
            bool newId = session.SessionId != firstId;

            sb.AppendLine("[8] RESTART FULLY RESETS THE SESSION");
            sb.AppendLine("    before: " + cpBefore + " checkpoints   after: "
                          + session.CheckpointsPassed + "   new session id=" + newId);
            sb.AppendLine("    state cleared=" + Pass(cleared) + "  fresh session id=" + Pass(newId));
            sb.AppendLine();
        }

        private static string Pass(bool b) { return b ? "PASS" : "**FAIL**"; }
    }
}
