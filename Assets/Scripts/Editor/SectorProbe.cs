using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using RenaultRacer.Core;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.UI;
using RenaultRacer.Vehicle;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Scripted verification of the sector timing, the personal bests and the minimap's
    /// derivation of the circuit. Editor-only, and development-only - nothing here ships.
    ///
    /// Two halves, and the split is deliberate:
    ///
    /// **A. THE REAL TRACK.** What <see cref="TrackBuilder"/> currently produces: how many
    /// sectors it declares, where the lines actually fall, whether the minimap's integrated
    /// path closes, how the fifteen turn badges land. These MEASURE rather than assert,
    /// because the circuit is being retuned concurrently and a probe that fails on a track
    /// mid-tune teaches nobody anything. The one hard assertion is that the layout declares
    /// three sectors, because that is the contract sector timing is built on.
    ///
    /// **B. THE RULES.** Gate order, the clock, restart, the personal-best update rules and
    /// the delta readout, all on a SYNTHETIC three-sector track with hand-driven positions.
    /// Deterministic on purpose: these tests are about this code being right, not about the
    /// circuit, and they must keep their meaning whatever shape the track settles into.
    /// </summary>
    public static class SectorProbe
    {
        private const float Step = 1f / 60f;

        public static string RunAll(HandlingProfile profile, GameConfig config)
        {
            StringBuilder sb = new StringBuilder(8192);
            sb.AppendLine("=============== SECTOR / PB / MINIMAP VERIFICATION ===============");

            TrackModel real = TrackBuilder.BuildMainTrack();
            sb.AppendLine("real track " + real.Count + " segments, " + real.Length.ToString("F0")
                          + " units");
            sb.AppendLine();

            // --- A. the real track --------------------------------------------------
            TestRealLayout(real, sb);
            TestMinimapPath(real, sb);
            TestMinimapSectorColouring(real, sb);
            TestBadgePlacement(real, sb);

            // --- B. the rules, on a synthetic 3-sector circuit -----------------------
            TrackModel synth = Synthetic(1200, new[] { 0, 400, 800 }, 4);
            sb.AppendLine("synthetic rig: " + synth.Count + " segments, sector lines at "
                          + "400 / 800, " + synth.Length.ToString("F0") + " units, "
                          + "4 checkpoints");
            sb.AppendLine();

            TestEachLineCrossedExactlyOnce(synth, profile, sb);
            TestGatesCannotBeSkipped(synth, profile, config, sb);
            TestSplitsSumToLapTime(synth, profile, sb);
            TestSplitsAreFrameRateIndependent(synth, profile, sb);
            TestRestartClearsSplits(synth, profile, sb);
            TestFirstValidLapCreatesTheBaseline(synth, profile, config, sb);
            TestInvalidLapDoesNotUpdateBests(synth, profile, config, sb);
            TestFasterAndSlowerSectorsRead(synth, profile, config, sb);
            TestDeltaReadout(sb);
            TestPlayerPrefsRoundTrip(synth, sb);

            sb.AppendLine("=================================================================");
            return sb.ToString();
        }

        // ================================================================ A. real track

        private static void TestRealLayout(TrackModel track, StringBuilder sb)
        {
            TrackLayout layout = track.Layout;
            int[] starts = layout.SectorStartSegment;

            sb.AppendLine("[1] THE TRACK DECLARES ITS OWN SECTOR LINES");
            sb.AppendLine("    sectors " + layout.SectorCount + "   turns " + layout.Turns.Length);
            for (int i = 0; i < starts.Length; i++)
                sb.AppendLine("    sector " + (i + 1) + " opens at segment " + starts[i]
                              + "   z " + layout.SectorStartZ(i).ToString("F0")
                              + "   lap fraction " + layout.FractionOf(starts[i]).ToString("F3"));

            bool ascending = starts[0] == 0;
            for (int i = 1; i < starts.Length; i++)
                if (starts[i] <= starts[i - 1] || starts[i] >= track.Count) ascending = false;

            sb.AppendLine("    three sectors=" + Pass(layout.SectorCount == 3)
                          + "  first line at segment 0=" + Pass(starts[0] == 0)
                          + "  strictly ascending and inside the lap=" + Pass(ascending));
            if (layout.SectorCount < 2)
                sb.AppendLine("    NOTE: the layout carries no sector lines yet. Sector "
                              + "timing degrades to one sector covering the lap; the rules "
                              + "below are still verified on the synthetic rig.");
            if (layout.Turns.Length != 15)
                sb.AppendLine("    NOTE: " + layout.Turns.Length + " turns, the circuit spec "
                              + "calls for 15. Turn badges follow whatever is there.");
            sb.AppendLine();
        }

        /// <summary>
        /// The minimap's path must come from the same curve data the car drives, and a
        /// closed circuit must close. Uses MinimapGeometry directly - no GameObject, no
        /// Canvas, no Play Mode.
        /// </summary>
        private static void TestMinimapPath(TrackModel track, StringBuilder sb)
        {
            const int n = 96;
            Vector2[] shape = new Vector2[n];
            int[] segAt = new int[n];
            Vector2 endpoint;
            int kept = MinimapGeometry.Integrate(track, n, shape, null, segAt, out endpoint);

            Vector2 centre;
            float scale;
            MinimapGeometry.Normalise(shape, kept, out centre, out scale);
            float closure = endpoint.magnitude * scale;

            // The last kept point must be within one stride of the end of the lap, or the
            // decimation has left a hole at the start/finish line.
            int lastSegment = kept > 0 ? segAt[kept - 1] : -1;
            int stride = track.Count / n;

            sb.AppendLine("[2] THE MINIMAP PATH IS THE DRIVEN PATH");
            sb.AppendLine("    asked for " + n + " points, kept " + kept
                          + "   first segment " + (kept > 0 ? segAt[0] : -1)
                          + "   last segment " + lastSegment + " of " + track.Count);
            sb.AppendLine("    closure error " + (closure * 100f).ToString("F2")
                          + "% of the map's extent");
            sb.AppendLine("    every point kept=" + Pass(kept == n)
                          + "  starts at segment 0=" + Pass(kept > 0 && segAt[0] == 0)
                          + "  covers the last stride=" + Pass(lastSegment >= track.Count - stride - 1)
                          + "  lap closes (<8%)=" + Pass(closure < 0.08f));
            if (closure >= 0.08f)
                sb.AppendLine("    NOTE: the integrated lap does not return to its start, so "
                              + "the minimap will draw an OPEN route with no closing span. "
                              + "That is MinimapView being honest about the track, not a "
                              + "minimap bug - the circuit does not close.");
            sb.AppendLine();
        }

        /// <summary>
        /// Every drawn span must be attributed to the sector the layout puts it in, and
        /// the colours must be the three the art sheet defines.
        /// </summary>
        private static void TestMinimapSectorColouring(TrackModel track, StringBuilder sb)
        {
            const int n = 96;
            Vector2[] shape = new Vector2[n];
            int[] segAt = new int[n];
            Vector2 endpoint;
            int kept = MinimapGeometry.Integrate(track, n, shape, null, segAt, out endpoint);

            TrackLayout layout = track.Layout;
            int[] perSector = new int[Mathf.Max(1, layout.SectorCount)];
            bool monotonic = true;
            int previous = 0;
            for (int i = 0; i < kept; i++)
            {
                int s = layout.SectorAtSegment(segAt[i]);
                if (s < previous) monotonic = false;
                previous = s;
                if (s >= 0 && s < perSector.Length) perSector[s]++;
            }

            sb.AppendLine("[3] SECTOR COLOUR COMES FROM THE LAYOUT");
            StringBuilder counts = new StringBuilder();
            bool allUsed = true;
            for (int i = 0; i < perSector.Length; i++)
            {
                Color32 c = MinimapArt.SectorColor(i);
                counts.Append("    sector ").Append(i + 1).Append(": ").Append(perSector[i])
                      .Append(" of ").Append(kept).Append(" spans   #")
                      .Append(Hex(c)).AppendLine();
                if (perSector[i] == 0) allUsed = false;
            }
            sb.Append(counts);
            sb.AppendLine("    sector index never goes backwards=" + Pass(monotonic)
                          + "  every sector drawn=" + Pass(allUsed));
            sb.AppendLine("    art loaded=" + MinimapArt.Available
                          + "   body #" + Hex(MinimapArt.BodyColor));
            sb.AppendLine();
        }

        /// <summary>
        /// Fifteen numbered badges on the minimap is the tightest thing on the HUD, so it
        /// is measured rather than eyeballed. Mirrors HudView constants.
        ///
        /// The panel is 260x140 rather than 160x160, and the map is fitted to the shape of
        /// the CIRCUIT inside it instead of to the largest square that fits - so this also
        /// reports how much of the panel the drawn map actually uses, which is the number
        /// the change was made for.
        /// </summary>
        /// <summary>Every badge inside the panel, per axis.</summary>
        private static bool InsideClamp(Vector2[] placed, int count, Vector2 clampHalf)
        {
            for (int i = 0; i < count; i++)
                if (Mathf.Abs(placed[i].x) > clampHalf.x + 0.01f
                 || Mathf.Abs(placed[i].y) > clampHalf.y + 0.01f) return false;
            return true;
        }

        private static void TestBadgePlacement(TrackModel track, StringBuilder sb)
        {
            const int n = 96;
            const float BadgeUnits = 12f, Offset = 10f, MaxDrift = 18f;
            const float PanelW = 260f, PanelH = 140f, MarkerInset = 6f, Padding = 8f;

            Vector2[] shape = new Vector2[n];
            int[] segAt = new int[n];
            Vector2 endpoint;
            int kept = MinimapGeometry.Integrate(track, n, shape, null, segAt, out endpoint);
            Vector2 centre, extent;
            float scale;
            MinimapGeometry.Normalise(shape, kept, out centre, out scale, out extent);

            TurnInfo[] turns = track.Layout.Turns;
            if (turns.Length == 0)
            {
                sb.AppendLine("    the layout carries no turns, so no badges are placed.");
                sb.AppendLine("    circuit has its 15 numbered corners=" + Pass(false));
                sb.AppendLine();
                return;
            }

            // The same rect maths MinimapView.Scaled does: the outline rect is the panel
            // less its inset on each side, and the shape is fitted inside that less the
            // padding, at ONE scale for both axes.
            float rectW = PanelW - MarkerInset * 2f;
            float rectH = PanelH - MarkerInset * 2f;
            float s = Mathf.Min((rectW - Padding * 2f) / Mathf.Max(0.0001f, extent.x),
                                (rectH - Padding * 2f) / Mathf.Max(0.0001f, extent.y));
            Vector2 clampHalf = new Vector2(rectW * 0.5f - BadgeUnits * 0.5f,
                                            rectH * 0.5f - BadgeUnits * 0.5f);

            float usedW = extent.x * s, usedH = extent.y * s;
            sb.AppendLine("[4] TURN BADGES SIT ON THEIR OWN APEXES");
            sb.AppendLine("    panel " + PanelW.ToString("F0") + "x" + PanelH.ToString("F0")
                          + "   inset " + MarkerInset.ToString("F0")
                          + "   padding " + Padding.ToString("F0")
                          + "   drawn map " + usedW.ToString("F0") + "x" + usedH.ToString("F0")
                          + " = " + (usedW / PanelW * 100f).ToString("F0") + "% of the width, "
                          + (usedH / PanelH * 100f).ToString("F0") + "% of the height");
            sb.AppendLine("    circuit shape after the quarter turn: "
                          + extent.x.ToString("F2") + " wide by " + extent.y.ToString("F2")
                          + " tall (longer side 1), uniform scale " + s.ToString("F1"));

            Vector2[] on = new Vector2[turns.Length];
            Vector2[] outward = new Vector2[turns.Length];
            Vector2[] placed = new Vector2[turns.Length];

            // The SAME functions MinimapView calls, so this measures the shipping
            // placement rather than a re-derivation of it that could quietly diverge.
            for (int i = 0; i < turns.Length; i++)
            {
                float f = track.Layout.FractionOf(turns[i].holdSegment);
                on[i] = MinimapGeometry.PointAt(shape, kept, f) * s;
                outward[i] = MinimapGeometry.OutwardAt(shape, kept, centre, f);
            }

            float worst = MinimapBadges.Place(on, outward, turns.Length, Offset, BadgeUnits,
                                              MaxDrift, clampHalf, placed);

            float furthest = 0f, drift = 0f;
            bool numbersInOrder = true;
            for (int i = 0; i < turns.Length; i++)
            {
                furthest = Mathf.Max(furthest, Mathf.Max(Mathf.Abs(placed[i].x), Mathf.Abs(placed[i].y)));
                drift = Mathf.Max(drift, (placed[i] - on[i]).magnitude);
                if (turns[i].number != i + 1) numbersInOrder = false;
            }

            sb.AppendLine("    " + turns.Length + " badges   closest pair "
                          + worst.ToString("F1") + " units apart (badge is "
                          + BadgeUnits.ToString("F0") + ")   furthest from centre "
                          + furthest.ToString("F1") + " of " + (rectW * 0.5f).ToString("F0")
                          + "   furthest from its own apex " + drift.ToString("F1"));
            sb.AppendLine("    turns numbered 1..n in driving order=" + Pass(numbersInOrder)
                          + "  every badge inside the map=" + Pass(InsideClamp(placed, turns.Length, clampHalf))
                          + "  no two badges overlap=" + Pass(worst >= BadgeUnits - 0.01f)
                          + "  every badge still beside its corner="
                          + Pass(drift <= MaxDrift + 0.01f));
            if (worst < BadgeUnits)
                sb.AppendLine("    NOTE: the closest pair still touch. The relaxation is "
                              + "bounded - a badge is never moved more than "
                              + MaxDrift.ToString("F0") + " units from the corner it names - "
                              + "so an overlap is preferred to a badge that has drifted away "
                              + "from its own apex, and both to dropping a corner number.");
            sb.AppendLine();
        }

        // ===================================================================== B. rules

        private static void TestEachLineCrossedExactlyOnce(TrackModel track, HandlingProfile p,
                                                           StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            int[] crossedAtTick = new int[session.Sectors.SectorCount];
            for (int i = 0; i < crossedAtTick.Length; i++) crossedAtTick[i] = -1;

            int last = 0, tick = 0, extra = 0;
            DriveLap(session, track, 45f, delegate
            {
                tick++;
                SectorTiming s = session.Sectors;
                if (s.CrossCount == last) return;
                if (s.CrossCount != last + 1) extra++;      // more than one gate in a tick
                last = s.CrossCount;
                int closed = s.LastClosedSector;
                if (closed >= 0 && closed < crossedAtTick.Length)
                {
                    if (crossedAtTick[closed] >= 0) extra++;  // the same line twice
                    crossedAtTick[closed] = tick;
                }
            });

            sb.AppendLine("[5] EVERY SECTOR LINE IS CROSSED EXACTLY ONCE, IN ORDER");
            for (int i = 0; i < crossedAtTick.Length; i++)
                sb.AppendLine("    sector " + (i + 1) + " closed on tick " + crossedAtTick[i]
                              + "   split " + session.Sectors.Split(i).ToString("F3") + "s");

            bool ordered = true;
            for (int i = 1; i < crossedAtTick.Length; i++)
                if (crossedAtTick[i] <= crossedAtTick[i - 1]) ordered = false;

            sb.AppendLine("    all " + crossedAtTick.Length + " closed="
                          + Pass(session.Sectors.SectorsCompleted == crossedAtTick.Length)
                          + "  none closed twice or two in a tick=" + Pass(extra == 0)
                          + "  closed in order=" + Pass(ordered)
                          + "  finish line is the last gate="
                          + Pass(Mathf.Approximately(session.Sectors.GateZ(crossedAtTick.Length - 1),
                                                     track.Length)));
            sb.AppendLine();
        }

        /// <summary>
        /// A single jump to the finish line. This is the case the naive
        /// <c>while (z &gt;= next) close++</c> form gets wrong - it would credit all three
        /// sectors at once with zero splits - and the case RaceProbe test [3] caught on
        /// the checkpoints.
        /// </summary>
        private static void TestGatesCannotBeSkipped(TrackModel track, HandlingProfile p,
                                                     GameConfig cfg, StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();
            session.Tick(45f, track.Length + 1f);

            SectorTiming s = session.Sectors;
            LapRejection r = RaceValidator.Validate(session, track, p, cfg);

            var bests = new PersonalBests(track.Layout, new MemoryBestsStore());
            bool banked = bests.CommitValidLap(session.ElapsedSeconds, s);

            sb.AppendLine("[6] SECTOR GATES CANNOT BE SKIPPED");
            sb.AppendLine("    jumped straight to the line: sectors closed "
                          + s.SectorsCompleted + "/" + s.SectorCount
                          + "   finished " + session.Finished + "   verdict " + r);
            sb.AppendLine("    the later gates stayed shut="
                          + Pass(s.SectorsCompleted < s.SectorCount)
                          + "  the last sector never closed=" + Pass(!s.Completed(s.SectorCount - 1))
                          + "  a partial set of splits banks nothing=" + Pass(!banked));
            sb.AppendLine();
        }

        private static void TestSplitsSumToLapTime(TrackModel track, HandlingProfile p,
                                                   StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();
            DriveLap(session, track, 45f, null);

            float sum = session.SectorSplitTotal;
            float lap = session.ElapsedSeconds;

            sb.AppendLine("[7] THE SPLITS ARE THE LAP");
            for (int i = 0; i < session.Sectors.SectorCount; i++)
                sb.AppendLine("    S" + (i + 1) + " " + session.Sectors.Split(i).ToString("F4") + "s");
            sb.AppendLine("    sum " + sum.ToString("F4") + "s   lap " + lap.ToString("F4")
                          + "s   difference " + (Mathf.Abs(sum - lap) * 1000f).ToString("F3") + " ms");
            sb.AppendLine("    splits sum to the lap time=" + Pass(Mathf.Abs(sum - lap) < 0.001f)
                          + "  every split positive=" + Pass(AllPositive(session.Sectors)));
            sb.AppendLine();
        }

        /// <summary>
        /// The splits come from the lap clock, which advances in whole simulation steps -
        /// so a 30 FPS player and a 60 FPS player must get the same splits, not just the
        /// same lap time. A separate stopwatch would fail exactly here.
        /// </summary>
        private static void TestSplitsAreFrameRateIndependent(TrackModel track, HandlingProfile p,
                                                              StringBuilder sb)
        {
            float[] a = SplitRun(track, p, 1);
            float[] b = SplitRun(track, p, 2);

            float worst = 0f;
            for (int i = 0; i < a.Length && i < b.Length; i++)
                worst = Mathf.Max(worst, Mathf.Abs(a[i] - b[i]));

            sb.AppendLine("[8] SPLITS ARE FRAME-RATE INDEPENDENT");
            for (int i = 0; i < a.Length; i++)
                sb.AppendLine("    S" + (i + 1) + "   60 FPS " + a[i].ToString("F3")
                              + "s   30 FPS " + b[i].ToString("F3") + "s");
            sb.AppendLine("    worst difference " + (worst * 1000f).ToString("F1") + " ms");
            // A gate is credited at the END of the tick that crossed it, so each boundary
            // can land one coarse tick (2 x Step at 30 FPS) either way and a split - being
            // a difference of two boundaries - up to two of them. Anything LARGER is not
            // quantisation, it is a second clock drifting, which is the failure this test
            // exists to catch.
            sb.AppendLine("    within the coarse run's quantisation (2 ticks)="
                          + Pass(worst <= Step * 4f + 0.0005f));
            sb.AppendLine();
        }

        private static float[] SplitRun(TrackModel track, HandlingProfile p, int stepsPerFrame)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            float dt = Step * stepsPerFrame;
            float z = 0f;
            float rate = track.Length / 45f;
            while (!session.Finished && session.ElapsedSeconds < 180f)
            {
                z += rate * dt;
                session.Tick(dt, z);
            }

            float[] splits = new float[session.Sectors.SectorCount];
            for (int i = 0; i < splits.Length; i++) splits[i] = session.Sectors.Split(i);
            return splits;
        }

        private static void TestRestartClearsSplits(TrackModel track, HandlingProfile p,
                                                    StringBuilder sb)
        {
            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();

            // Two-thirds of a lap: sector 1 closed, sector 2 closed, sector 3 open.
            float z = 0f;
            float rate = track.Length / 45f;
            while (z < track.Length * 0.85f)
            {
                z += rate * Step;
                session.Tick(Step, z);
            }
            int before = session.Sectors.SectorsCompleted;
            float splitBefore = session.Sectors.Split(0);

            session.Begin();     // restart, exactly as GameDirector does

            SectorTiming s = session.Sectors;
            bool cleared = s.SectorsCompleted == 0
                        && s.CrossCount == 0
                        && s.LastClosedSector == -1
                        && s.LastSplit == 0f
                        && s.Split(0) == 0f
                        && !s.Completed(0);

            sb.AppendLine("[9] RESTART CLEARS THE CURRENT ATTEMPT'S SPLITS");
            sb.AppendLine("    before: " + before + " sectors closed, S1 "
                          + splitBefore.ToString("F3") + "s   after: "
                          + s.SectorsCompleted + " closed, S1 " + s.Split(0).ToString("F3") + "s");
            sb.AppendLine("    banked at least one split first=" + Pass(before > 0)
                          + "  everything cleared=" + Pass(cleared));
            sb.AppendLine();
        }

        private static void TestFirstValidLapCreatesTheBaseline(TrackModel track, HandlingProfile p,
                                                                GameConfig cfg, StringBuilder sb)
        {
            var store = new MemoryBestsStore();
            var bests = new PersonalBests(track.Layout, store);

            // During the first attempt there is nothing to compare with.
            bool noBaselineDuringFirstLap = !bests.HasBaselineLap;
            string firstReadout = null;
            for (int i = 0; i < bests.SectorCount; i++)
            {
                if (bests.HasBaselineSector(i)) noBaselineDuringFirstLap = false;
                if (firstReadout == null)
                    firstReadout = SectorDeltaView.TextFor(bests.SectorDelta(i, 10f),
                                                           bests.HasBaselineSector(i));
            }

            var session = new RaceSession(track, p.maxSpeed);
            session.Begin();
            session.StartRacing();
            DriveLap(session, track, 45f, null);
            LapRejection r = RaceValidator.Validate(session, track, p, cfg);
            bool banked = r == LapRejection.None
                       && bests.CommitValidLap(session.ElapsedSeconds, session.Sectors);

            bests.BeginAttempt();     // the NEXT attempt starts here
            bool baselineNow = bests.HasBaselineLap;
            for (int i = 0; i < bests.SectorCount; i++)
                if (!bests.HasBaselineSector(i)) baselineNow = false;

            sb.AppendLine("[10] THE FIRST VALID LAP ESTABLISHES THE BASELINE");
            sb.AppendLine("    first attempt: baseline lap=" + bests.HasBaselineLap
                          + "   readout with no baseline \"" + firstReadout
                          + "\" colour " + Hex(SectorDeltaView.ColourFor(0f, false)));
            sb.AppendLine("    lap " + RaceSession.FormatTime(session.ElapsedSeconds)
                          + "   verdict " + r + "   banked " + banked
                          + "   stored lap " + StoredLapText(bests));
            sb.AppendLine("    nothing to compare against on lap one="
                          + Pass(noBaselineDuringFirstLap)
                          + "  shows \"--\" rather than a fabricated delta="
                          + Pass(firstReadout == "--")
                          + "  the accepted lap became the baseline=" + Pass(banked && baselineNow));
            sb.AppendLine();
        }

        private static void TestInvalidLapDoesNotUpdateBests(TrackModel track, HandlingProfile p,
                                                             GameConfig cfg, StringBuilder sb)
        {
            var store = new MemoryBestsStore();
            var bests = new PersonalBests(track.Layout, store);

            // A slow but honest lap first, so there IS something to protect.
            var honest = new RaceSession(track, p.maxSpeed);
            honest.Begin();
            honest.StartRacing();
            DriveLap(honest, track, 60f, null);
            bests.CommitValidLap(honest.ElapsedSeconds, honest.Sectors);

            float lapBefore, s1Before;
            bests.StoredLap(out lapBefore);
            bests.StoredSector(0, out s1Before);

            // Now a MUCH faster lap that the validator rejects. This is the exploit the
            // rule exists for: cut the lap short, bank a fake sector 1, discard the lap.
            var cheat = new RaceSession(track, p.maxSpeed);
            cheat.Begin();
            cheat.StartRacing();
            float z = 0f;
            float rate = track.Length / 12f;              // far under the physical floor
            while (!cheat.Finished && cheat.ElapsedSeconds < 60f)
            {
                z += rate * Step;
                cheat.Tick(Step, z);
            }
            LapRejection r = RaceValidator.Validate(cheat, track, p, cfg);

            // GameUi only calls CommitValidLap from GameState.Finished, which is only
            // reached when the verdict is None. Reproduce that gate here.
            bool wouldBank = r == LapRejection.None;
            if (wouldBank) bests.CommitValidLap(cheat.ElapsedSeconds, cheat.Sectors);

            float lapAfter, s1After;
            bests.StoredLap(out lapAfter);
            bests.StoredSector(0, out s1After);

            sb.AppendLine("[11] AN INVALID LAP UPDATES NOTHING");
            sb.AppendLine("    honest lap " + honest.ElapsedSeconds.ToString("F2")
                          + "s   rejected lap " + cheat.ElapsedSeconds.ToString("F2")
                          + "s (faster)   verdict " + r);
            sb.AppendLine("    stored lap " + lapBefore.ToString("F3") + "s -> "
                          + lapAfter.ToString("F3") + "s   stored S1 "
                          + s1Before.ToString("F3") + "s -> " + s1After.ToString("F3") + "s");
            sb.AppendLine("    the fast lap was rejected=" + Pass(r != LapRejection.None)
                          + "  best lap unchanged=" + Pass(Mathf.Approximately(lapBefore, lapAfter))
                          + "  best S1 unchanged=" + Pass(Mathf.Approximately(s1Before, s1After)));
            sb.AppendLine();
        }

        /// <summary>
        /// The headline behaviour: a sector faster than the stored best reads as a GREEN
        /// POSITIVE gain, a slower one as a RED NEGATIVE loss, and the comparison is
        /// against the best as it stood before the attempt began.
        /// </summary>
        private static void TestFasterAndSlowerSectorsRead(TrackModel track, HandlingProfile p,
                                                           GameConfig cfg, StringBuilder sb)
        {
            var store = new MemoryBestsStore();
            var bests = new PersonalBests(track.Layout, store);

            var first = new RaceSession(track, p.maxSpeed);
            first.Begin();
            first.StartRacing();
            DriveLap(first, track, 50f, null);
            bests.CommitValidLap(first.ElapsedSeconds, first.Sectors);

            // Second attempt, driven at a different pace in each sector: quicker through
            // sector 1, slower through the last one.
            bests.BeginAttempt();
            float baselineS1 = bests.BaselineSector(0);
            float baselineLast = bests.BaselineSector(bests.SectorCount - 1);

            var second = new RaceSession(track, p.maxSpeed);
            second.Begin();
            second.StartRacing();

            int lastCross = 0;
            float[] delta = new float[second.Sectors.SectorCount];
            string[] readout = new string[delta.Length];
            Color[] colour = new Color[delta.Length];
            bool[] hadBaseline = new bool[delta.Length];

            float z = 0f;
            SectorTiming st = second.Sectors;
            while (!second.Finished && second.ElapsedSeconds < 180f)
            {
                // Faster in sector 1, slower in the last: rate switches on the gate.
                int sector = st.CurrentSector;
                float seconds = sector == 0 ? 40f : (sector == st.SectorCount - 1 ? 70f : 50f);
                z += (track.Length / seconds) * Step;
                second.Tick(Step, z);

                if (st.CrossCount == lastCross) continue;
                lastCross = st.CrossCount;
                int closed = st.LastClosedSector;
                hadBaseline[closed] = bests.HasBaselineSector(closed);
                delta[closed] = bests.SectorDelta(closed, st.Split(closed));
                readout[closed] = SectorDeltaView.TextFor(delta[closed], hadBaseline[closed]);
                colour[closed] = SectorDeltaView.ColourFor(delta[closed], hadBaseline[closed]);
            }

            int lastIdx = st.SectorCount - 1;
            bool gainGreen = delta[0] > 0f && readout[0] != null && readout[0][0] == '+'
                          && colour[0] == SectorDeltaView.Gain;
            bool lossRed = delta[lastIdx] < 0f && readout[lastIdx] != null && readout[lastIdx][0] == '-'
                        && colour[lastIdx] == SectorDeltaView.Loss;

            sb.AppendLine("[12] FASTER READS GREEN AND POSITIVE, SLOWER READS RED AND NEGATIVE");
            for (int i = 0; i < delta.Length; i++)
                sb.AppendLine("    S" + (i + 1) + " best " + bests.BaselineSector(i).ToString("F2")
                              + "s   now " + st.Split(i).ToString("F2")
                              + "s   shows \"" + readout[i] + "\" in #" + Hex(colour[i]));
            sb.AppendLine("    S1 " + baselineS1.ToString("F2") + "s -> "
                          + st.Split(0).ToString("F2") + "s (faster)   S" + (lastIdx + 1) + " "
                          + baselineLast.ToString("F2") + "s -> " + st.Split(lastIdx).ToString("F2")
                          + "s (slower)");
            sb.AppendLine("    a faster sector gains (+, green)=" + Pass(gainGreen)
                          + "  a slower sector loses (-, red)=" + Pass(lossRed)
                          + "  compared against the PRE-ATTEMPT best="
                          + Pass(Mathf.Approximately(baselineS1, first.Sectors.Split(0))));
            sb.AppendLine();
        }

        /// <summary>
        /// The readout itself: sign convention, rounding, and the fact that no culture can
        /// put a comma in it.
        /// </summary>
        private static void TestDeltaReadout(StringBuilder sb)
        {
            System.Globalization.CultureInfo saved =
                System.Threading.Thread.CurrentThread.CurrentCulture;
            bool noComma = true;
            string sample = "(sl-SI not available on this runtime)";
            try
            {
                // Slovenian: a decimal COMMA. This is the culture a phone in the campaign's
                // own market reports, and the one that would break a ToString-built string.
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("sl-SI");
                sample = SectorDeltaView.TextFor(1.4f, true);
                for (int t = 0; t < 300; t += 7)
                    if (SectorDeltaView.TextFor(t / 10f, true).IndexOf(',') >= 0) noComma = false;
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                // A trimmed ICU build has no sl-SI. The formatter is still culture-free by
                // construction; this particular demonstration of it just cannot run.
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }

            string gain = SectorDeltaView.TextFor(1.4f, true);
            string loss = SectorDeltaView.TextFor(-1.6f, true);
            string none = SectorDeltaView.TextFor(0f, false);
            string big = SectorDeltaView.TextFor(412f, true);
            string rounded = SectorDeltaView.TextFor(1.449f, true);

            sb.AppendLine("[13] THE DELTA READOUT");
            sb.AppendLine("    +1.4 -> \"" + gain + "\"   -1.6 -> \"" + loss
                          + "\"   no baseline -> \"" + none + "\"");
            sb.AppendLine("    1.449 -> \"" + rounded + "\"   412 -> \"" + big
                          + "\"   under sl-SI -> \"" + sample + "\"");
            sb.AppendLine("    gain reads +1.4 s=" + Pass(gain == "+1.4 s")
                          + "  loss reads -1.6 s=" + Pass(loss == "-1.6 s")
                          + "  no baseline reads --=" + Pass(none == "--"));
            sb.AppendLine("    rounds to a tenth=" + Pass(rounded == "+1.4 s")
                          + "  overflow does not fake a tenth=" + Pass(big == "> +30 s")
                          + "  culture-invariant (no comma under sl-SI)=" + Pass(noComma));
            sb.AppendLine();
        }

        /// <summary>
        /// The shipping store really is PlayerPrefs, which under WebGL is the browser's
        /// own storage. Written and read back for real, then removed - this test must not
        /// leave a fake record in the editor's prefs.
        /// </summary>
        private static void TestPlayerPrefsRoundTrip(TrackModel track, StringBuilder sb)
        {
            var bests = new PersonalBests(track.Layout, new PlayerPrefsStore());
            bests.ClearAll();

            var timing = new SectorTiming(track.Layout, track.Length);
            timing.Reset();
            // Close every gate by hand at a known time, so the values written are known.
            float t = 0f;
            float lastZ = -1f;
            for (int i = 0; i < timing.SectorCount; i++)
            {
                t += 10f + i;                       // 10, 21, 33 -> splits 10, 11, 12
                timing.Tick(lastZ, timing.GateZ(i), t);
                lastZ = timing.GateZ(i);
            }

            bool wrote = bests.CommitValidLap(t, timing);

            // A FRESH instance, so the values can only come back through the store.
            var reloaded = new PersonalBests(track.Layout, new PlayerPrefsStore());
            float lap;
            bool hasLap = reloaded.StoredLap(out lap);
            bool sectorsMatch = true;
            for (int i = 0; i < timing.SectorCount; i++)
            {
                float v;
                if (!reloaded.StoredSector(i, out v) || Mathf.Abs(v - timing.Split(i)) > 0.0005f)
                    sectorsMatch = false;
            }

            // A slower lap must not overwrite a faster one.
            bool refusedWorse = !reloaded.CommitValidLap(t + 5f, timing);

            sb.AppendLine("[14] PERSONAL BESTS ROUND-TRIP THROUGH PLAYERPREFS");
            sb.AppendLine("    key " + bests.LapKey);
            sb.AppendLine("    wrote lap " + t.ToString("F2") + "s, read back "
                          + lap.ToString("F2") + "s   splits "
                          + timing.Split(0).ToString("F1") + " / "
                          + (timing.SectorCount > 1 ? timing.Split(1).ToString("F1") : "-") + " / "
                          + (timing.SectorCount > 2 ? timing.Split(2).ToString("F1") : "-"));
            sb.AppendLine("    written=" + Pass(wrote)
                          + "  lap survives a reload=" + Pass(hasLap && Mathf.Abs(lap - t) < 0.0005f)
                          + "  every sector survives=" + Pass(sectorsMatch)
                          + "  a worse time is refused=" + Pass(refusedWorse));

            bests.ClearAll();
            float gone;
            sb.AppendLine("    cleared afterwards=" + Pass(!bests.StoredLap(out gone)));
            sb.AppendLine();
        }

        // ==================================================================== plumbing

        /// <summary>
        /// A synthetic circuit: flat, straight, with sector lines and checkpoints exactly
        /// where this probe wants them. It exists so the RULES can be tested against known
        /// numbers while the real circuit is still being tuned.
        /// </summary>
        private static TrackModel Synthetic(int segments, int[] sectorStarts, int checkpoints)
        {
            Segment[] segs = new Segment[segments];
            for (int i = 0; i < segments; i++)
            {
                segs[i].curve = 0f;
                segs[i].y = 0f;
                segs[i].surface = Surface.Road;
                segs[i].barrierLeft = -2f;
                segs[i].barrierRight = 2f;
                segs[i].checkpointId = -1;
            }
            for (int c = 0; c < checkpoints; c++)
            {
                int at = (int)((c + 1) * (long)segments / (checkpoints + 1));
                segs[at].checkpointId = c;
            }
            segs[segments - 1].isFinishGate = true;

            TurnInfo[] turns = new TurnInfo[0];
            return new TrackModel(segs, new TrackLayout(turns, sectorStarts, segments));
        }

        /// <summary>
        /// Advance a session at a constant pace until it finishes. <paramref name="onTick"/>
        /// runs after every tick, so a test can watch the crossings as they happen.
        /// </summary>
        private static void DriveLap(RaceSession session, TrackModel track, float seconds,
                                     Action onTick)
        {
            float z = 0f;
            float rate = track.Length / seconds;
            while (!session.Finished && session.ElapsedSeconds < 300f)
            {
                z += rate * Step;
                session.Tick(Step, z);
                if (onTick != null) onTick();
            }
        }

        private static bool AllPositive(SectorTiming s)
        {
            for (int i = 0; i < s.SectorsCompleted; i++)
                if (s.Split(i) <= 0f) return false;
            return true;
        }

        private static string StoredLapText(PersonalBests b)
        {
            float v;
            return b.StoredLap(out v) ? v.ToString("F3") + "s" : "none";
        }

        private static string Hex(Color32 c)
        {
            return c.r.ToString("X2") + c.g.ToString("X2") + c.b.ToString("X2");
        }

        private static string Hex(Color c) { return Hex((Color32)c); }

        private static string Pass(bool b) { return b ? "PASS" : "**FAIL**"; }
    }

    /// <summary>
    /// Ways to actually RUN <see cref="SectorProbe"/>: from the menu, and from the command
    /// line, so the sector rules can be checked after a track retune without opening the
    /// Editor by hand. Same shape as <see cref="RaceProbeMenu"/>.
    ///
    ///   Unity.exe -batchmode -quit -projectPath . \
    ///             -executeMethod RenaultRacer.EditorTools.SectorProbeMenu.RunFromCommandLine \
    ///             -logFile -
    /// </summary>
    public static class SectorProbeMenu
    {
        private const string ReportPath = "Logs/sector-probe.txt";

        [MenuItem("Renault Racer/Sector + PB probe", false, 43)]
        public static void Run()
        {
            Debug.Log(BuildReport());
        }

        /// <summary>Batch entry point. Exits non-zero if anything failed.</summary>
        public static void RunFromCommandLine()
        {
            string report;
            try { report = BuildReport(); }
            catch (Exception e)
            {
                Console.WriteLine("sector probe threw: " + e);
                EditorApplication.Exit(2);
                return;
            }

            Console.WriteLine(report);
            try
            {
                Directory.CreateDirectory("Logs");
                File.WriteAllText(ReportPath, report);
                Console.WriteLine("[sector probe] written to " + ReportPath);
            }
            catch (Exception e) { Console.WriteLine("could not write the report: " + e.Message); }

            int failures = 0;
            for (int i = report.IndexOf("**FAIL**", StringComparison.Ordinal); i >= 0;
                 i = report.IndexOf("**FAIL**", i + 1, StringComparison.Ordinal)) failures++;

            Console.WriteLine("[sector probe] " + (failures == 0 ? "ALL PASS"
                                                                 : failures + " FAILURE(S)"));
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static string BuildReport()
        {
            var profile = AssetDatabase.LoadAssetAtPath<HandlingProfile>(
                "Assets/ScriptableObjects/Handling/Production.asset");
            var config = AssetDatabase.LoadAssetAtPath<GameConfig>(
                "Assets/ScriptableObjects/Config/GameConfig.asset");
            if (profile == null) return "no Production HandlingProfile";
            if (config == null) return "no GameConfig";
            return SectorProbe.RunAll(profile, config);
        }
    }
}
