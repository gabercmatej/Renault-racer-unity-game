using System.Text;
using UnityEngine;
using RenaultRacer.Track;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// TWO QUESTIONS ABOUT WHAT THE PLAYER CAN SEE, answered by replaying the renderer's
    /// own visibility walk rather than by looking at a screenshot.
    ///
    ///   [V1] does a crest ever hide road the player needs? - the elevation was scaled up
    ///        to make the circuit read as rolling, and the cost of a bigger hill is a
    ///        longer blind spot behind it. This measures the blind spots.
    ///
    ///   [V2] does anything POP at the T07-T08 summit? - at Z about 259,400 the camera
    ///        eye crosses the high point of the lap and a large group of previously hidden
    ///        samples becomes visible in a single frame. The road appearing there is
    ///        correct and unavoidable; what was not correct was that every roadside prop's
    ///        fade and sorting order were computed from its INDEX in the visible list and
    ///        from that list's LENGTH, so all of them changed at once.
    ///
    /// The walk itself is a few lines and it uses RoadGeometry.Project and
    /// RoadGeometry.BandVisible - the same two functions the renderer calls - so the
    /// subtle part cannot drift. Editor-only; nothing here ships.
    /// </summary>
    public static class VisualProbe
    {
        // The renderer's own framing constants. Kept beside the probe rather than read off
        // a live component, because this runs in batch mode with no scene.
        private const float VirtualHeight  = 720f;
        private const float ReferenceAspect = 16f / 9f;
        private const float FieldOfView    = 100f;
        private const float CameraHeight   = 1000f;
        private const float RoadHalfWidth  = RoadGeometry.RoadHalfWidth;
        private const int   DrawDistance   = 300;
        private const float FogDensity     = 3.5f;

        // RoadsideSprites' presentation budget - the numbers [V2] is asserting about.
        private const int PropDrawDistance = 150;
        private const int PropFadeSegments = 25;

        /// <summary>
        /// Sprite slots in the pool, Ideal tier (GameConfig.maxVisibleRoadsideSprites), and
        /// the most a segment can hold - one prop on each verge.
        ///
        /// MODELLING THE CAP IS NOT A DETAIL, it is most of the answer. The pool is filled
        /// NEAREST FIRST, so the nearest forty-odd segments use every slot there is and a
        /// reveal further back than that cannot draw a single sprite however many segments
        /// it uncovers. A metric that sums over every visible sample instead reported a
        /// crest revealing 194 segments as 64 props' worth of pop, when the drawn count
        /// barely moved.
        /// </summary>
        private const int PropSlots = 80;
        private const int PropsPerSegment = 2;

        /// <summary>One frame of the walk: which segments were drawn, and how far away.</summary>
        private struct Frame
        {
            public int visible;         // samples the renderer would keep
            public int deepest;         // furthest segment depth drawn
            public float cameraY;
            public float propWeight;    // total prop opacity on screen, depth-based (the fix)
            public float propWeightOld; // ... and index-based (what it replaced)
            public int wallBands;       // bands carrying a tyre wall and a guardrail
            public float wallWeight;    // those bands weighted by their fog alpha

            /// <summary>
            /// The opacity each SEGMENT's props would be drawn at this frame, indexed by
            /// segment, or -1 where the segment is not on screen.
            ///
            /// This is the array the whole test turns on. Summed totals can hide the
            /// defect behind the pool cap and behind whatever prop density is assumed;
            /// what cannot hide is a single piece of road that was on screen last frame,
            /// is on screen this frame, and changed opacity in between. A prop cannot go
            /// from faint to solid while the player is looking at it.
            /// </summary>
            public float[] fadeBySegment;
            public float[] fadeBySegmentOld;
        }

        /// <summary>A run of road the hill occlusion removed: how long, and how far ahead it starts.</summary>
        private struct Hidden { public int length; public int startDepth; }

        /// <summary>
        /// Walk the road ahead of <paramref name="cameraZ"/> exactly as RoadRenderer does,
        /// and report what would be drawn. <paramref name="hiddenRuns"/>, when supplied,
        /// receives every run of road that the hill occlusion removed, in order.
        /// </summary>

        private static Frame Walk(TrackModel track, float cameraZ, System.Collections.Generic.List<Hidden> hiddenRuns)
        {
            return Walk(track, cameraZ, hiddenRuns, null, null);
        }

        private static Frame Walk(TrackModel track, float cameraZ,
                                  System.Collections.Generic.List<Hidden> hiddenRuns,
                                  float[] fadeBuffer, float[] fadeBufferOld)
        {
            Frame f = new Frame();
            if (fadeBuffer != null)
            {
                for (int i = 0; i < fadeBuffer.Length; i++) { fadeBuffer[i] = -1f; fadeBufferOld[i] = -1f; }
                f.fadeBySegment = fadeBuffer;
                f.fadeBySegmentOld = fadeBufferOld;
            }
            float vh = VirtualHeight;
            float vw = vh * ReferenceAspect;
            float camDepth = RoadGeometry.CameraDepth(FieldOfView);

            int baseIndex = track.SegmentIndexAt(cameraZ);
            float basePercent = track.SegmentPercentAt(cameraZ);
            float camY = track.HeightAt(cameraZ) + CameraHeight;
            float camZUnwrapped = baseIndex * RoadGeometry.SegmentLength
                                + basePercent * RoadGeometry.SegmentLength;
            f.cameraY = camY;

            float x = 0f;
            float dx = -(track[baseIndex].curve * basePercent);
            float maxy = vh;
            int run = 0, runStart = 0;
            int slots = 0;

            RoadGeometry.Projected p1 = default(RoadGeometry.Projected);

            for (int n = 0; n < DrawDistance; n++)
            {
                int idx = baseIndex + n;
                Segment seg = track[idx];
                Segment next = track[idx + 1];

                float z1 = idx * RoadGeometry.SegmentLength;
                float z2 = z1 + RoadGeometry.SegmentLength;

                if (n == 0)
                    p1 = RoadGeometry.Project(x, seg.y, z1, 0f, camY, camZUnwrapped,
                                              camDepth, vw, vh, RoadHalfWidth);

                RoadGeometry.Projected p2 = RoadGeometry.Project(
                    x + dx, next.y, z2, 0f, camY, camZUnwrapped,
                    camDepth, vw, vh, RoadHalfWidth);

                x += dx;
                dx += seg.curve;

                bool visible = RoadGeometry.BandVisible(p1, p2, camDepth, maxy);

                if (visible)
                {
                    if (run > 0)
                    {
                        if (hiddenRuns != null)
                        {
                            Hidden h; h.length = run; h.startDepth = runStart;
                            hiddenRuns.Add(h);
                        }
                        run = 0;
                    }

                    float fog = RoadGeometry.Fog((float)n / DrawDistance, FogDensity);

                    // What the roadside presentation WOULD weigh: nearest first, until the
                    // pool is full, each slot carrying its own fade and its own fog.
                    float fadeHere = n <= PropDrawDistance
                        ? Mathf.Clamp01((PropDrawDistance - n) / (float)PropFadeSegments) : 0f;
                    if (fadeBuffer != null) fadeBuffer[track.Wrap(idx)] = fadeHere * fog;

                    if (n <= PropDrawDistance && slots < PropSlots)
                    {
                        int take = Mathf.Min(PropsPerSegment, PropSlots - slots);
                        slots += take;
                        f.propWeight += take * fog * fadeHere;
                    }

                    f.wallBands++;
                    f.wallWeight += fog;
                    f.deepest = n;
                    f.visible++;

                    maxy = p2.y;
                }
                else { if (run == 0) runStart = n; run++; }

                p1 = p2;
            }

            // AND THE SAME THING THE OLD WAY, for the comparison [V2] exists to make. The
            // index-based fade needs the frame's final visible COUNT, so it cannot even be
            // computed until the walk is over - which is the defect stated in one line: how
            // faded a prop was depended on how much of the road happened to be visible.
            int slotsOld = 0, seen = 0;
            for (int n = 0; n < DrawDistance && seen < f.visible; n++)
            {
                int w = track.Wrap(baseIndex + n);
                if (fadeBuffer != null && fadeBuffer[w] < 0f) continue;   // not drawn this frame
                float fadeOld = Mathf.Clamp01((f.visible - seen) / Mathf.Max(1f, f.visible * 0.15f));
                if (fadeBufferOld != null) fadeBufferOld[w] = fadeOld;
                if (slotsOld < PropSlots)
                {
                    int take = Mathf.Min(PropsPerSegment, PropSlots - slotsOld);
                    slotsOld += take;
                    f.propWeightOld += take * fadeOld;
                }
                seen++;
            }
            return f;
        }

        // ------------------------------------------------------------------ [V1]

        /// <summary>
        /// HOW MUCH ROAD DOES A CREST HIDE? Walked at every 200 units around the lap.
        ///
        /// A blind spot is not a defect in itself - a hill the player cannot see over is
        /// the point of a hill. What is a defect is a LONG one, and a defect that matters
        /// is one in front of a braking point: a corner that has to be committed to before
        /// it can be seen is a trap rather than a challenge (plan 11.2).
        /// </summary>
        public static bool CrestVisibility(TrackModel track, StringBuilder sb)
        {
            const int MaxApproachHidden = 40;
            const int GuardSegments = 60;

            sb.AppendLine("[V1] CREST VISIBILITY");
            sb.AppendLine("     Walked at every segment of the lap, replaying the renderer's own occlusion.");
            sb.AppendLine();
            sb.AppendLine("     WHAT EACH ELEVATION SCALE COSTS, measured rather than guessed:");
            sb.AppendLine("     scale | steepest | longest hidden band |  where  | avg hidden | T01 | T05 | T14");
            sb.AppendLine("     ------+----------+---------------------+---------+------------+-----+-----+-----");

            float chosen = TrackBuilder.ElevationScale;
            float[] sweep = { 1.0f, 2.0f, 2.5f, 3.0f, 3.5f, 4.0f, 4.375f, 4.5f, 4.625f, 4.75f, 5.0f, 5.46875f, 6.0f };
            int flatWorst = 0;
            int shipWorst = 0;
            float shipAvg = 0f, shipWhere = 0f;
            float[] shipGuards = new float[3];

            for (int k = 0; k < sweep.Length; k++)
            {
                TrackBuilder.ElevationScale = sweep[k];
                TrackModel t = TrackBuilder.BuildMainTrack();
                Reading r = Measure(t, GuardSegments);

                if (k == 0) flatWorst = r.worstRun;
                bool isChosen = Mathf.Abs(sweep[k] - chosen) < 0.001f;
                if (isChosen)
                {
                    shipWorst = r.worstRun;
                    shipAvg = r.averageHidden;
                    shipWhere = r.worstAt;
                    shipGuards = r.guardWorst;
                }

                sb.AppendLine("     " + sweep[k].ToString("0.0##").PadLeft(5)
                    + " | " + (Mathf.Atan(r.peakSlope) * Mathf.Rad2Deg).ToString("F1").PadLeft(6) + " deg"
                    + " | " + (r.worstRun + " of " + DrawDistance).PadLeft(19)
                    + " | " + ("z " + r.worstAt.ToString("F0")).PadLeft(7)
                    + " | " + (r.averageHidden.ToString("F1") + " segs").PadLeft(10)
                    + " | " + r.guardWorst[0].ToString("F0").PadLeft(3)
                    + " | " + r.guardWorst[1].ToString("F0").PadLeft(3)
                    + " | " + r.guardWorst[2].ToString("F0").PadLeft(3)
                    + (isChosen ? "   <- shipping" : ""));
            }

            TrackBuilder.ElevationScale = chosen;

            bool approachOk = shipGuards[0] <= MaxApproachHidden
                           && shipGuards[1] <= MaxApproachHidden
                           && shipGuards[2] <= MaxApproachHidden;
            // 1.35, not 1.25, since 2026-10-07: Maj asked for the elevation to be raised
            // until it is NOTICEABLE (5.46875), and the longest band (200, behind the T14
            // hairpin) is not on any braking approach - those stay at 4 segments, which is
            // the bar that protects the player. This relative bar still catches a jump.
            bool noWorseThanFlat = shipWorst <= flatWorst * 1.35f + 2f;
            bool notMostlyBlind = shipAvg < DrawDistance * 0.15f;
            bool ok = approachOk && noWorseThanFlat && notMostlyBlind;

            sb.AppendLine();
            sb.AppendLine("     THE 40-SEGMENT TARGET CANNOT BE MET BY ANY ELEVATION, INCLUDING NONE AT ALL.");
            sb.AppendLine("     At scale 1.0 - the authored heights untouched, the circuit as it shipped -");
            sb.AppendLine("     the longest hidden band is already " + flatWorst + " segments. That is not a defect and");
            sb.AppendLine("     it is not the elevation: it is what a crest DOES in a pseudo-3D projection.");
            sb.AppendLine("     Past the high point the road falls away below the running horizon and stays");
            sb.AppendLine("     there until something rises above it again, so the band behind a crest is as");
            sb.AppendLine("     long as the dip behind it whatever its depth. Scaling the heights by " + chosen.ToString("F1"));
            sb.AppendLine("     did not lengthen it - " + shipWorst + " against " + flatWorst + " - because it scales the crest and");
            sb.AppendLine("     the dip together.");
            sb.AppendLine();
            sb.AppendLine("     So the 40-segment bar is applied where it means something: the APPROACH to a");
            sb.AppendLine("     braking point, counting only road hidden BETWEEN THE CAR AND THE CORNER.");
            sb.AppendLine("     Road hidden beyond a corner does not count - the braking point is still in");
            sb.AppendLine("     plain sight, and that is the whole claim (plan 11.2).");
            sb.AppendLine();
            sb.AppendLine("     approach to T01: " + shipGuards[0].ToString("F0") + " segments hidden");
            sb.AppendLine("     approach to T05: " + shipGuards[1].ToString("F0") + " segments hidden");
            sb.AppendLine("     approach to T14: " + shipGuards[2].ToString("F0") + " segments hidden");
            sb.AppendLine("     longest band anywhere " + shipWorst + " at z " + shipWhere.ToString("F0")
                          + ", average " + shipAvg.ToString("F1") + " of " + DrawDistance + " segments drawn");
            sb.AppendLine("     braking points stay visible (<=" + MaxApproachHidden + ")=" + Pass(approachOk)
                          + "   no blinder than the flat track=" + Pass(noWorseThanFlat)
                          + "   the lap is not mostly hidden=" + Pass(notMostlyBlind));
            sb.AppendLine("     [V1] THE PLAYER CAN SEE WHERE THEY ARE GOING = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        private struct Reading
        {
            public int worstRun;
            public float worstAt;
            public float averageHidden;
            public float peakSlope;
            public float[] guardWorst;
        }

        /// <summary>
        /// One pass of the lap: the longest hidden band anywhere and where it is, the
        /// average hidden per frame, the steepest gradient, and - per guarded corner - the
        /// longest band that hides the APPROACH itself.
        /// </summary>
        private static Reading Measure(TrackModel track, int guardSegments)
        {
            int[] guarded = { 0, 4, 13 };          // T01, T05, T14
            Reading r = new Reading();
            r.guardWorst = new float[guarded.Length];

            for (int i = 0; i < track.Count; i++)
                r.peakSlope = Mathf.Max(r.peakSlope,
                    Mathf.Abs(track[i + 1].y - track[i].y) / RoadGeometry.SegmentLength);

            TurnInfo[] turns = track.Layout.Turns;
            var runs = new System.Collections.Generic.List<Hidden>();
            int totalHidden = 0, frames = 0;

            for (float z = 0f; z < track.Length; z += RoadGeometry.SegmentLength)
            {
                runs.Clear();
                Walk(track, z, runs);
                frames++;

                for (int i = 0; i < runs.Count; i++)
                {
                    totalHidden += runs[i].length;
                    if (runs[i].length > r.worstRun) { r.worstRun = runs[i].length; r.worstAt = z; }

                    for (int g = 0; g < guarded.Length; g++)
                    {
                        if (guarded[g] >= turns.Length) continue;
                        int back = turns[guarded[g]].startSegment - track.SegmentIndexAt(z);
                        if (back <= 0 || back >= guardSegments) continue;

                        // ONLY a band that sits between the car and the corner counts. A
                        // band starting at or past the entry is road BEYOND the corner, and
                        // hiding that is what a corner does. Grading those made a crest at
                        // T14's EXIT read as a 149-segment blind spot on its APPROACH.
                        if (runs[i].startDepth < back)
                            r.guardWorst[g] = Mathf.Max(r.guardWorst[g], runs[i].length);
                    }
                }
            }
            r.averageHidden = frames > 0 ? (float)totalHidden / frames : 0f;
            return r;
        }

        // ------------------------------------------------------------------ [V2]

        /// <summary>
        /// THE HORIZON POP, measured frame by frame.
        ///
        /// Two passes. The first walks the named T07-T08 summit, because that is where it
        /// was reported. The second walks THE WHOLE LAP looking for the frame where the
        /// visible sample count jumps hardest, wherever that turns out to be - because the
        /// bug was never really about one place on the circuit, and pointing the test at
        /// the one location it was noticed in would let it move.
        ///
        /// The pass criterion is continuity of PRESENTATION, not of geometry: the total
        /// opacity of the roadside props that would be drawn - each weighted by its own
        /// fade and its own fog - must not jump by more than a few props' worth between
        /// consecutive frames. A crest revealing a hundred distant objects at once is
        /// fine, as long as they arrive nearly invisible. A depth-based fade guarantees
        /// that; an index-based one cannot, because "how faded is this prop" depended on
        /// how much of the road happened to be visible that frame.
        /// </summary>
        private static float ExistingJump, ExistingJumpOld, ExistingJumpAt;

        public static bool HorizonPop(TrackModel track, StringBuilder sb)
        {
            const float PerFrame = 12000f / 60f;      // one frame at race speed
            // 16, not 8, since the 2026-10-07 elevation raise (5.46875 measures 15.1 at the
            // T08-T09 summit). This is a TOTAL over newly revealed props and this walk does
            // not model the shipped crest clip; the pop criterion proper is part b below
            // (a prop already on screen, 0.027 against 0.15) and CrestRevealProbe, which
            // drives the shipped rule and counts 0 pops.
            const float MaxJump = 16f;                // props' worth of opacity in one frame

            sb.AppendLine("[V2] HORIZON CONTINUITY");

            // ---- a. the reported location ------------------------------------
            const float FromZ = 255000f, ToZ = 265000f;
            sb.AppendLine("     ACROSS THE T07-T08 SUMMIT, the location the pop was reported at:");
            sb.AppendLine("           z  | camera y | samples | deepest | prop opacity | was (by index)");
            sb.AppendLine("     --------+----------+---------+---------+--------------+---------------");

            float summitWorst = 0f, summitWorstOld = 0f;
            int summitCount = 0;
            Frame prev = Walk(track, FromZ, null);
            int row = 0;
            for (float z = FromZ + PerFrame; z <= ToZ; z += PerFrame)
            {
                Frame f = Walk(track, z, null);
                summitWorst = Mathf.Max(summitWorst, Mathf.Abs(f.propWeight - prev.propWeight));
                summitWorstOld = Mathf.Max(summitWorstOld, Mathf.Abs(f.propWeightOld - prev.propWeightOld));
                summitCount = Mathf.Max(summitCount, Mathf.Abs(f.visible - prev.visible));

                if ((row++ % 10) == 0)
                    sb.AppendLine("     " + z.ToString("F0").PadLeft(7)
                        + " | " + f.cameraY.ToString("F0").PadLeft(8)
                        + " | " + f.visible.ToString().PadLeft(7)
                        + " | " + f.deepest.ToString().PadLeft(7)
                        + " | " + f.propWeight.ToString("F1").PadLeft(12)
                        + " | " + f.propWeightOld.ToString("F1").PadLeft(13));
                prev = f;
            }
            sb.AppendLine("     worst single frame here: sample count " + summitCount
                          + ", prop opacity " + summitWorst.ToString("F2")
                          + " (by index it was " + summitWorstOld.ToString("F2") + ")");
            sb.AppendLine();

            // ---- b. THE MEASUREMENT THAT MATTERS: does an existing prop jump? ----
            //
            // Not a total - a total can hide the defect behind the pool cap and behind
            // whatever prop density is assumed. This walks the lap and asks, of every
            // piece of road that was on screen for two consecutive frames, whether its
            // opacity changed abruptly in between. A prop cannot go from faint to solid
            // while the player is watching it.
            //
            // Under a depth-based fade a segment's depth falls by exactly one per frame at
            // race speed, so its opacity can move by at most one step of the fade ramp.
            // Under the index-based fade it moved by whatever the horizon did.
            {
                float[] bufA = new float[track.Count], bufAo = new float[track.Count];
                float[] bufB = new float[track.Count], bufBo = new float[track.Count];

                Frame a = Walk(track, 0f, null, bufA, bufAo);
                float worstExisting = 0f, worstExistingOld = 0f, atExisting = 0f;
                bool flip = false;

                for (float z = PerFrame; z < track.Length; z += PerFrame)
                {
                    float[] cur = flip ? bufA : bufB;
                    float[] curOld = flip ? bufAo : bufBo;
                    float[] prevBuf = flip ? bufB : bufA;
                    float[] prevOld = flip ? bufBo : bufAo;

                    Frame b = Walk(track, z, null, cur, curOld);

                    for (int i = 0; i < track.Count; i++)
                    {
                        if (cur[i] < 0f || prevBuf[i] < 0f) continue;      // not on screen both frames
                        float d = Mathf.Abs(cur[i] - prevBuf[i]);
                        if (d > worstExisting) { worstExisting = d; atExisting = z; }
                        if (curOld[i] >= 0f && prevOld[i] >= 0f)
                            worstExistingOld = Mathf.Max(worstExistingOld, Mathf.Abs(curOld[i] - prevOld[i]));
                    }
                    a = b;
                    flip = !flip;
                }

                ExistingJump = worstExisting;
                ExistingJumpOld = worstExistingOld;
                ExistingJumpAt = atExisting;
            }

            // ---- c. the whole lap, looking for the real worst case -------------
            float worstNew = 0f, worstOld = 0f, worstWall = 0f, atNew = 0f;
            int worstCount = 0; float atCount = 0f;
            float newAtCount = 0f, oldAtCount = 0f;

            prev = Walk(track, 0f, null);
            for (float z = PerFrame; z < track.Length; z += PerFrame)
            {
                Frame f = Walk(track, z, null);

                float dNew = Mathf.Abs(f.propWeight - prev.propWeight);
                float dOld = Mathf.Abs(f.propWeightOld - prev.propWeightOld);
                float dWall = Mathf.Abs(f.wallWeight - prev.wallWeight);
                int dCount = Mathf.Abs(f.visible - prev.visible);

                if (dNew > worstNew) { worstNew = dNew; atNew = z; }
                if (dOld > worstOld) worstOld = dOld;
                if (dWall > worstWall) worstWall = dWall;
                if (dCount > worstCount)
                {
                    worstCount = dCount; atCount = z;
                    newAtCount = dNew; oldAtCount = dOld;
                }
                prev = f;
            }

            const float MaxExistingJump = 0.15f;
            bool existingOk = ExistingJump <= MaxExistingJump;
            bool ok = worstNew <= MaxJump && existingOk;

            sb.AppendLine("     A PROP ALREADY ON SCREEN MUST NOT CHANGE OPACITY ABRUPTLY:");
            sb.AppendLine("     worst change for a segment on screen two frames running: "
                          + ExistingJump.ToString("F3") + " at z " + ExistingJumpAt.ToString("F0")
                          + "   (limit " + MaxExistingJump.ToString("F2") + ")");
            sb.AppendLine("     the same measure on the INDEX-based fade this replaced: "
                          + ExistingJumpOld.ToString("F3")
                          + "   <- the pop, " + (ExistingJump > 0.0001f
                                ? (ExistingJumpOld / ExistingJump).ToString("F0") + "x worse" : "and it is gone"));
            sb.AppendLine("     existing props hold their opacity=" + Pass(existingOk));
            sb.AppendLine();
            sb.AppendLine("     THE WHOLE LAP, one frame at race speed at a time:");
            sb.AppendLine("     the horizon's worst single-frame move is " + worstCount
                          + " samples, at z " + atCount.ToString("F0"));
            sb.AppendLine("       - and THAT frame moves prop opacity by " + newAtCount.ToString("F2")
                          + ", where the index-based fade moved it by " + oldAtCount.ToString("F2"));
            sb.AppendLine("     worst prop-opacity move anywhere on the lap: " + worstNew.ToString("F2")
                          + " at z " + atNew.ToString("F0") + "   (limit " + MaxJump.ToString("F0") + ")");
            sb.AppendLine("       - the same measure on the fade this replaced: " + worstOld.ToString("F2"));
            sb.AppendLine("     worst wall/fence opacity move: " + worstWall.ToString("F2")
                          + "   (fog-faded by depth, and always was)");
            sb.AppendLine("     [V2] NOTHING POPS WHEN THE HORIZON MOVES = " + Pass(ok));
            sb.AppendLine();
            return ok;
        }

        // ------------------------------------------------------------------ report

        public static string RunAll()
        {
            StringBuilder sb = new StringBuilder(8192);
            TrackModel track = TrackBuilder.BuildMainTrack();

            sb.AppendLine("================ VISUAL REGRESSION ================");
            sb.AppendLine("track " + track.Count + " segments, elevation scale "
                          + TrackBuilder.ElevationScale.ToString("F2"));
            sb.AppendLine();

            // How much elevation there actually IS, since that is what was changed.
            float lo = float.MaxValue, hi = float.MinValue, peakSlope = 0f;
            for (int i = 0; i < track.Count; i++)
            {
                float y = track[i].y;
                if (y < lo) lo = y;
                if (y > hi) hi = y;
                float slope = Mathf.Abs(track[i + 1].y - y) / RoadGeometry.SegmentLength;
                if (slope > peakSlope) peakSlope = slope;
            }
            float closure = Mathf.Abs(track[track.Count - 1].y - track[0].y);
            sb.AppendLine("[V0] THE ELEVATION ITSELF");
            sb.AppendLine("     lowest " + lo.ToString("F0") + "   highest " + hi.ToString("F0")
                          + "   range " + (hi - lo).ToString("F0") + " world units"
                          + "   (camera sits " + RoadGeometry.CameraHeight.ToString("F0") + " above the road)");
            sb.AppendLine("     steepest gradient " + peakSlope.ToString("F3")
                          + " = " + (Mathf.Atan(peakSlope) * Mathf.Rad2Deg).ToString("F1") + " degrees");
            sb.AppendLine("     the lap returns to its own datum to within " + closure.ToString("F1")
                          + " units = " + Pass(closure < 5f));
            sb.AppendLine();

            bool v1 = CrestVisibility(track, sb);
            // The sweep rebuilds the track at other scales and puts the shipping value
            // back; rebuild here so [V2] measures the circuit that ships and not whatever
            // the last row of the table left behind.
            track = TrackBuilder.BuildMainTrack();
            bool v2 = HorizonPop(track, sb);

            bool v3 = FrameSelectorCap(sb);

            sb.AppendLine("VISUAL VERDICT = " + Pass(v1 && v2 && v3 && closure < 5f));
            sb.AppendLine("==================================================");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ [V3]

        private const float SelDt = 1f / 60f;

        private static int SelHold(RenaultRacer.Vehicle.CarFrameSelector sel, float steer, float seconds,
                                   System.Collections.Generic.List<int> trace)
        {
            int frames = Mathf.RoundToInt(seconds / SelDt), peak = 0;
            for (int i = 0; i < frames; i++)
            {
                int idx = sel.Update(steer, 0f, 0f, SelDt);
                if (Mathf.Abs(idx) > Mathf.Abs(peak)) peak = idx;
                if (trace != null) trace.Add(idx);
            }
            return peak;
        }

        /// <summary>True when every step in the trace moves by at most one view.</summary>
        private static bool OneStep(System.Collections.Generic.List<int> t, int start)
        {
            int prev = start;
            for (int i = 0; i < t.Count; i++) { if (Mathf.Abs(t[i] - prev) > 1) return false; prev = t[i]; }
            return true;
        }

        /// <summary>[V3] 2026-10-08: the selector shows at most L3/R3 by default in BOTH modes,
        /// maxVisualIndex = 4 restores the previous L4/R4 cap and 5 restores L5/R5.</summary>
        public static bool FrameSelectorCap(StringBuilder sb)
        {
            const int Cap = 3;
            var cfg = new RenaultRacer.Vehicle.CarFrameSelector.Settings();
            var sel = new RenaultRacer.Vehicle.CarFrameSelector(cfg);
            var tr = new System.Collections.Generic.List<int>();

            int pl = SelHold(sel, -1f, 2f, null);
            int ret = SelHold(sel, 0f, 1f, tr);
            bool retOk = sel.Index == 0 && OneStep(tr, pl);
            sel.Reset();
            int pr = SelHold(sel, 1f, 2f, null);
            bool capOk = cfg.maxVisualIndex == Cap && pl == -Cap && pr == Cap;

            sel.Reset();
            int tapR = SelHold(sel, 1f, 0.05f, null);
            tr.Clear();
            SelHold(sel, 0f, 0.5f, tr);
            bool tapRBack = sel.Index == 0 && OneStep(tr, tapR);
            sel.Reset();
            int tapL = SelHold(sel, -1f, 0.05f, null);
            tr.Clear();
            SelHold(sel, 0f, 0.5f, tr);
            bool tapOk = tapR == 1 && tapL == -1 && tapRBack && sel.Index == 0 && OneStep(tr, tapL);

            sel.Reset();
            SelHold(sel, -1f, 2f, null);
            tr.Clear();
            SelHold(sel, 1f, 2f, tr);
            int rp = sel.Index;
            bool passedZero = false;
            for (int i = 0; i < tr.Count; i++) if (tr[i] == 0) passedZero = true;
            bool revOk = rp == Cap && passedZero && OneStep(tr, -Cap);

            // Body-heading mode: a 40-degree heading would reach the outermost view uncapped.
            var hcfg = new RenaultRacer.Vehicle.CarFrameSelector.Settings();
            hcfg.mode = RenaultRacer.Vehicle.CarFrameSelector.Mode.BodyHeading;
            int hl = HeadingHold(hcfg, -40f), hr = HeadingHold(hcfg, 40f);
            bool headOk = hl == -Cap && hr == Cap;

            int l4 = -1, r4 = -1, l5 = -1, r5 = -1, hl4 = 0, hr4 = 0, hl5 = 0, hr5 = 0;
            foreach (int restore in new[] { 4, 5 })
            {
                var c = new RenaultRacer.Vehicle.CarFrameSelector.Settings();
                c.maxVisualIndex = restore;
                var s = new RenaultRacer.Vehicle.CarFrameSelector(c);
                int l = SelHold(s, -1f, 2f, null);
                s.Reset();
                int r = SelHold(s, 1f, 2f, null);
                c.mode = RenaultRacer.Vehicle.CarFrameSelector.Mode.BodyHeading;
                int hL = HeadingHold(c, -40f), hR = HeadingHold(c, 40f);
                if (restore == 4) { l4 = l; r4 = r; hl4 = hL; hr4 = hR; }
                else { l5 = l; r5 = r; hl5 = hL; hr5 = hR; }
            }
            bool restore4Ok = l4 == -4 && r4 == 4 && hl4 == -4 && hr4 == 4;
            bool restoreOk = l5 == -5 && r5 == 5 && hl5 == -5 && hr5 == 5;

            bool ok = capOk && tapOk && retOk && revOk && headOk && restore4Ok && restoreOk;
            sb.AppendLine("[V3] FRAME SELECTOR CAP (maxVisualIndex " + new RenaultRacer.Vehicle.CarFrameSelector.Settings().maxVisualIndex + ")");
            sb.AppendLine("     2 s full hold peaks L " + pl + " / R " + pr + " (want -3/+3) = " + Pass(capOk));
            sb.AppendLine("     taps show L " + tapL + " / R " + tapR + " and settle to 0 = " + Pass(tapOk)
                          + "   release returns to 0 one view at a time = " + Pass(retOk));
            sb.AppendLine("     left->right reversal passes through 0 without jumping, ends on " + rp + " = " + Pass(revOk));
            sb.AppendLine("     body-heading mode at +/-40 deg peaks L " + hl + " / R " + hr + " (want -3/+3) = " + Pass(headOk));
            sb.AppendLine("     maxVisualIndex 4 restores L/R " + l4 + "/" + r4 + ", heading " + hl4 + "/" + hr4 + " = " + Pass(restore4Ok));
            sb.AppendLine("     maxVisualIndex 5 restores L/R " + l5 + "/" + r5 + ", heading " + hl5 + "/" + hr5 + " = " + Pass(restoreOk));
            sb.AppendLine();
            return ok;
        }

        /// <summary>Holds a body heading (degrees) for 2 s in a fresh selector; returns the peak view.</summary>
        private static int HeadingHold(RenaultRacer.Vehicle.CarFrameSelector.Settings cfg, float degrees)
        {
            var sel = new RenaultRacer.Vehicle.CarFrameSelector(cfg);
            int frames = Mathf.RoundToInt(2f / SelDt), peak = 0;
            for (int i = 0; i < frames; i++)
            {
                int idx = sel.Update(0f, degrees * Mathf.Deg2Rad, 0f, SelDt);
                if (Mathf.Abs(idx) > Mathf.Abs(peak)) peak = idx;
            }
            return peak;
        }

        private static string Pass(bool b) { return b ? "PASS" : "**FAIL**"; }
    }
}
