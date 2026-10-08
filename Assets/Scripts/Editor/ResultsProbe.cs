using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Race;
using RenaultRacer.Track;
using RenaultRacer.UI;

namespace RenaultRacer.EditorTools
{
    /// <summary>
    /// Verification of the animated results card.
    ///   -executeMethod RenaultRacer.EditorTools.ResultsProbe.RunFromCommandLine
    /// Report: Logs/results-probe.txt, pass marker "[results probe] ALL PASS". Needs a
    /// graphics device (it also renders captures to Logs/results-capture/).
    ///
    /// A. ResultsModel semantics against a MemoryBestsStore - what the player reads.
    /// B. ResultsView timeline - the view driven directly (Step), no real clock: final
    ///    state by 2.4 s, skip, replay reset, time frozen behind the rotate gate, dt clamp.
    /// C. Captures of the final card and mid-animation frames at four screen sizes.
    /// </summary>
    public static class ResultsProbe
    {
        private static readonly StringBuilder Sb = new StringBuilder();
        private static int _fails;
        private static TrackModel _track;

        public static void RunFromCommandLine()
        {
            Sb.Length = 0; _fails = 0;
            try
            {
                _track = TrackBuilder.BuildMainTrack();
                Check("layout declares three sectors", _track.Layout != null && _track.Layout.SectorCount == 3);
                Section("A. model semantics"); ModelTests();
                Section("B. view timeline"); ViewTests();
                Section("C. captures"); Captures();
            }
            catch (Exception e)
            {
                Sb.AppendLine("**FAIL** exception: " + e);
                _fails++;
            }

            Sb.AppendLine(_fails == 0 ? "[results probe] ALL PASS" : "[results probe] " + _fails + " check(s) **FAIL**ed");
            Directory.CreateDirectory("Logs");
            File.WriteAllText("Logs/results-probe.txt", Sb.ToString());
            Console.WriteLine(Sb.ToString());
            EditorApplication.Exit(_fails == 0 ? 0 : 1);
        }

        private static void Section(string s) { Sb.AppendLine(); Sb.AppendLine("--- " + s); }

        private static void Check(string what, bool ok)
        {
            Sb.AppendLine("  " + (ok ? "PASS " : "**FAIL** ") + what);
            if (!ok) _fails++;
        }

        private static bool Near(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;
        }

        // ================================================================= A. model

        private static SectorTiming Timing(params float[] splits)
        {
            SectorTiming st = new SectorTiming(_track.Layout, _track.Length);
            float cum = 0f;
            for (int i = 0; i < splits.Length; i++)
            {
                cum += splits[i];
                float g = st.GateZ(i);
                st.Tick(g - 1f, g + 0.5f, cum);
            }
            return st;
        }

        private static PersonalBests Bests(float seedLap, params float[] seedSplits)
        {
            PersonalBests b = new PersonalBests(_track.Layout, new MemoryBestsStore());
            if (seedLap > 0f) b.CommitValidLap(seedLap, Timing(seedSplits));
            b.BeginAttempt();
            return b;
        }

        private static void ModelTests()
        {
            // --- first lap on this circuit: nothing to compare with
            SectorTiming first = Timing(18.4f, 17.3f, 17.4f);
            float lap = 53.1f;
            Check("splits sum to the lap", Mathf.Abs(first.Split(0) + first.Split(1) + first.Split(2) - lap) < 0.001f);
            PersonalBests empty = Bests(0f);
            ResultsModel m = ResultsModel.Build(lap, first, empty);
            Check("first lap: PRVI REKORD!", m.Kind == BestKind.FirstRecord && m.BestHeading == "PRVI REKORD!");
            Check("first lap: no difference shown", !m.BestDiff.Present);
            Check("first lap: lap text " + m.LapText, m.LapText == "00:53.1" && m.BestTimeText == "00:53.1");
            Check("first lap: exactly two checkpoint rows", m.Sectors.Length == 2 && m.SectorLabels.Length == 2);
            Check("first lap: cumulative times 18,4 / 35,7, neutral white, no +0,0 [" + m.Sectors[0].Text + " " + m.Sectors[1].Text + "]",
                  m.Sectors[0].Text == "18,4 s" && m.Sectors[1].Text == "35,7 s"
                  && m.Sectors[0].Tone == ResultTone.Neutral && m.Sectors[1].Tone == ResultTone.Neutral
                  && !m.SectorHasBaseline[0] && !m.SectorHasBaseline[1]);
            Check("checkpoint labels PO SEKTORJU 1 / 2", m.SectorLabels[0] == "PO SEKTORJU 1" && m.SectorLabels[1] == "PO SEKTORJU 2");
            Check("no SEKTOR 3 / TVOJ label in the model", m.SectorLabels[0] != "SEKTOR 3" && m.SectorLabels[1] != "SEKTOR 3"
                  && UiCopy.FinalTime == "KONČNI ČAS");

            // the timing overload and the plain-array overload agree
            ResultsModel m2 = ResultsModel.Build(lap, new[] { 18.4f, 17.3f, 17.4f }, empty);
            Check("SectorTiming and float[] overloads agree", m2.Sectors[1].Text == m.Sectors[1].Text && m2.Sectors.Length == 2);

            // --- faster than the best: new best, green improvement = previous - lap
            PersonalBests prev = Bests(53.6f, 19.9f, 17.4f, 16.3f);
            ResultsModel nb = ResultsModel.Build(53.1f, first, prev);
            Check("faster: NOV NAJBOLJSI CAS", nb.Kind == BestKind.NewBest && nb.BestHeading == "NOV NAJBOLJŠI ČAS!");
            Check("faster: new time shown " + nb.BestTimeText, nb.BestTimeText == "00:53.1");
            Check("faster: improvement +0,5 s green (prev - lap) [" + nb.BestDiff.Text + "]",
                  nb.BestDiff.Present && nb.BestDiff.Text == "+0,5 s" && nb.BestDiff.Tone == ResultTone.Gain
                  && nb.BestDiff.FinalUnits == Mathf.RoundToInt((prev.BaselineLap - 53.1f) * 10f));
            Check("checkpoint deltas vs best lap's cumulative: +1,5 green / +1,6 green [" + nb.Sectors[0].Text + " " + nb.Sectors[1].Text + "]",
                  nb.Sectors[0].Text == "+1,5 s" && nb.Sectors[0].Tone == ResultTone.Gain
                  && nb.Sectors[1].Text == "+1,6 s" && nb.Sectors[1].Tone == ResultTone.Gain
                  && nb.SectorHasBaseline[0] && nb.SectorHasBaseline[1]);
            Check("tones map to the sector pill's colours",
                  Near(ResultsView.ColourFor(ResultTone.Gain), SectorDeltaView.ColourFor(1f, true))
                  && Near(ResultsView.ColourFor(ResultTone.Loss), SectorDeltaView.ColourFor(-1f, true)));

            // --- slower: the stored best, a red negative difference
            PersonalBests fast = Bests(52.9f, 18f, 17f, 17.9f);
            ResultsModel sl = ResultsModel.Build(53.1f, first, fast);
            Check("slower: plain heading, stored best, not a new best",
                  sl.Kind == BestKind.Best && sl.BestHeading == "NAJBOLJŠI ČAS" && sl.BestTimeText == "00:52.9");
            Check("slower: -0,2 s red (best - lap) [" + sl.BestDiff.Text + "]",
                  sl.BestDiff.Text == "-0,2 s" && sl.BestDiff.Tone == ResultTone.Loss);
            Check("slower: checkpoints red -0,4 / -0,7 [" + sl.Sectors[0].Text + " " + sl.Sectors[1].Text + "]",
                  sl.Sectors[0].Text == "-0,4 s" && sl.Sectors[0].Tone == ResultTone.Loss
                  && sl.Sectors[1].Text == "-0,7 s" && sl.Sectors[1].Tone == ResultTone.Loss);

            // --- equal: not a new best
            PersonalBests same = Bests(53.1f, 18.4f, 17.3f, 17.4f);
            ResultsModel eq = ResultsModel.Build(53.1f, first, same);
            Check("equal: not a new best, neutral zero [" + eq.BestDiff.Text + "]",
                  eq.Kind == BestKind.Best && eq.BestDiff.Tone == ResultTone.Neutral && eq.BestDiff.Text == "0,0 s");
            Check("equal: checkpoint zeros are neutral", eq.Sectors[0].Tone == ResultTone.Neutral && eq.Sectors[0].Text == "0,0 s"
                  && eq.Sectors[1].Tone == ResultTone.Neutral && eq.Sectors[1].Text == "0,0 s");

            // --- never +0,0 for a nonzero delta
            PersonalBests close = Bests(53.14f, 18.44f, 17.3f, 17.4f);
            ResultsModel cl = ResultsModel.Build(53.1f, first, close);
            Check("0,04 s improvement shows two decimals [" + cl.BestDiff.Text + "]",
                  cl.BestDiff.Text == "+0,04 s" && cl.BestDiff.Tone == ResultTone.Gain);
            Check("checkpoint +0,04 s [" + cl.Sectors[0].Text + " " + cl.Sectors[1].Text + "]", cl.Sectors[0].Text == "+0,04 s" && cl.Sectors[1].Text == "+0,04 s");

            // --- a missing split: "--", and nothing after it can be a cumulative time
            ResultsModel ms = ResultsModel.Build(53.1f, new[] { 18.4f, float.NaN, 17.4f }, prev);
            Check("missing split 2: row 1 delta, row 2 '--' [" + ms.Sectors[0].Text + " " + ms.Sectors[1].Text + "]",
                  ms.Sectors[0].Text == "+1,5 s" && ms.Sectors[1].Text == "--" && ms.Sectors[1].Tone == ResultTone.Neutral);
            ResultsModel ms0 = ResultsModel.Build(53.1f, new[] { float.NaN, 17.3f, 17.4f }, prev);
            Check("missing split 1: both rows '--'", ms0.Sectors[0].Text == "--" && ms0.Sectors[1].Text == "--");

            // --- old save: a best lap exists but its checkpoint keys do not
            MemoryBestsStore oldStore = new MemoryBestsStore();
            PersonalBests oldSave = new PersonalBests(_track.Layout, oldStore);
            oldSave.CommitValidLap(53.6f, Timing(19.9f, 17.4f, 16.3f));
            oldStore.Delete(oldSave.CheckpointKey(0)); oldStore.Delete(oldSave.CheckpointKey(1));
            oldSave.BeginAttempt();
            ResultsModel om = ResultsModel.Build(53.1f, first, oldSave);
            Check("old save: best lap known, no checkpoint baseline", oldSave.HasBaselineLap && !oldSave.HasBaselineCheckpoint(0) && !oldSave.HasBaselineCheckpoint(1));
            Check("old save: neutral cumulative 18,4 / 35,7, never +0,0; best row still works [" + om.Sectors[0].Text + " " + om.Sectors[1].Text + "]",
                  om.Sectors[0].Text == "18,4 s" && om.Sectors[1].Text == "35,7 s"
                  && om.Sectors[0].Tone == ResultTone.Neutral && om.Sectors[1].Tone == ResultTone.Neutral
                  && om.Kind == BestKind.NewBest && om.BestDiff.Text == "+0,5 s");

            // --- the checkpoint keys follow the best lap, and only the best lap
            MemoryBestsStore cpStore = new MemoryBestsStore();
            PersonalBests cp = new PersonalBests(_track.Layout, cpStore);
            cp.CommitValidLap(53.6f, Timing(19.9f, 17.4f, 16.3f));
            cp.BeginAttempt();
            Check("first commit stores cumulative checkpoints 19,9 / 37,3",
                  cp.HasBaselineCheckpoint(0) && cp.HasBaselineCheckpoint(1)
                  && Mathf.Abs(cp.BaselineCheckpoint(0) - 19.9f) < 0.001f && Mathf.Abs(cp.BaselineCheckpoint(1) - 37.3f) < 0.001f);
            cp.CommitValidLap(55.0f, Timing(18.0f, 17.0f, 20.0f));     // slower lap, but fast splits
            cp.BeginAttempt();
            Check("a slower lap does not touch the checkpoint keys",
                  Mathf.Abs(cp.BaselineCheckpoint(0) - 19.9f) < 0.001f && Mathf.Abs(cp.BaselineCheckpoint(1) - 37.3f) < 0.001f);
            cp.CommitValidLap(53.1f, Timing(18.4f, 17.3f, 17.4f));
            cp.BeginAttempt();
            Check("a new best lap replaces them (18,4 / 35,7); delta = baseline - cumulative",
                  Mathf.Abs(cp.BaselineCheckpoint(0) - 18.4f) < 0.001f && Mathf.Abs(cp.BaselineCheckpoint(1) - 35.7f) < 0.001f
                  && Mathf.Abs(cp.CheckpointDelta(1, 36.7f) + 1f) < 0.001f);
            bool bad = false;
            for (float v = -2f; v <= 2f; v += 0.001f)
            {
                if (Mathf.Abs(v) < ResultsModel.ZeroEpsilon) continue;
                string t = ResultsModel.Signed(v).Text;
                if (t == "+0,0 s" || t == "-0,0 s" || t == "0,0 s" || t == "+0,00 s" || t == "-0,00 s") { bad = true; Sb.AppendLine("    bad at " + v + ": " + t); break; }
            }
            Check("no nonzero delta in [-2,2] prints as zero", !bad);
            Check("zero and float noise are neutral 0,0 s", ResultsModel.Signed(0f).Text == "0,0 s" && ResultsModel.Signed(1e-6f).Tone == ResultTone.Neutral);
            Check("decimal comma, hand-built digits", ResultsModel.Signed(12.34f).Text == "+12,3 s" && ResultsModel.Signed(-0.3f).Text == "-0,3 s");

            // ============ checkpoint-reference verification ============

            // --- the spec example: reference 20,0 / 38,0, current durations 19,0 / 20,0 -> 19,0 / 39,0
            PersonalBests specRef = Bests(57f, 20f, 18f, 19f);
            Check("reference stored as CUMULATIVE 20,0 / 38,0",
                  Mathf.Abs(specRef.BaselineCheckpoint(0) - 20f) < 0.001f && Mathf.Abs(specRef.BaselineCheckpoint(1) - 38f) < 0.001f);
            ResultsModel spec = ResultsModel.Build(58f, Timing(19f, 20f, 19f), specRef);
            Check("mixed: cp1 +1,0 green, cp2 (cumulative 39,0 vs 38,0) -1,0 red - not sector 2 alone [" + spec.Sectors[0].Text + " " + spec.Sectors[1].Text + "]",
                  spec.Sectors[0].Text == "+1,0 s" && spec.Sectors[0].Tone == ResultTone.Gain
                  && spec.Sectors[1].Text == "-1,0 s" && spec.Sectors[1].Tone == ResultTone.Loss);
            Check("mixed: lap 1,0 slower than the reference lap -> stored best shown, red -1,0 s",
                  spec.Kind == BestKind.Best && spec.BestDiff.Text == "-1,0 s" && spec.BestDiff.Tone == ResultTone.Loss && spec.BestTimeText == "00:57.0");

            // --- small differences: rounding and colour
            PersonalBests tiny = Bests(57f, 19.004f, 18.996f, 19f);                      // reference cumulative 19,004 / 38,000
            ResultsModel tm = ResultsModel.Build(57f, Timing(19f, 19f, 19f), tiny);       // current 19,0 / 38,0
            Check("0,004 s gained at cp1 shows +0,01 s green (never 0,0) [" + tm.Sectors[0].Text + "]",
                  tm.Sectors[0].Text == "+0,01 s" && tm.Sectors[0].Tone == ResultTone.Gain);
            Check("exact tie at cp2 shows neutral 0,0 s, no sign [" + tm.Sectors[1].Text + "]",
                  tm.Sectors[1].Text == "0,0 s" && tm.Sectors[1].Tone == ResultTone.Neutral && tm.Sectors[1].Sign == '\0');
            Check("0,0004 s is float noise: neutral 0,0 s; -0,004 s is red -0,01 s",
                  ResultsModel.Signed(0.0004f).Text == "0,0 s" && ResultsModel.Signed(0.0004f).Tone == ResultTone.Neutral
                  && ResultsModel.Signed(-0.004f).Text == "-0,01 s" && ResultsModel.Signed(-0.004f).Tone == ResultTone.Loss);

            // --- the card is built BEFORE the commit; the snapshot stays frozen until BeginAttempt
            PersonalBests order = Bests(53.6f, 19.9f, 17.4f, 16.3f);
            SectorTiming ordLap = Timing(18.4f, 17.3f, 17.4f);
            ResultsModel built = ResultsModel.Build(53.1f, ordLap, order);
            order.CommitValidLap(53.1f, ordLap);
            ResultsModel stale = ResultsModel.Build(53.1f, ordLap, order);
            Check("snapshot stays frozen after a commit until BeginAttempt (no self-comparison)",
                  built.Kind == BestKind.NewBest && stale.BestDiff.Text == built.BestDiff.Text && stale.Sectors[0].Text == built.Sectors[0].Text);
            order.BeginAttempt();
            ResultsModel after = ResultsModel.Build(53.1f, ordLap, order);
            Check("after BeginAttempt the committed lap is the reference: equal lap reads 0,0 s neutral",
                  after.Kind == BestKind.Best && after.BestDiff.Text == "0,0 s" && after.Sectors[0].Text == "0,0 s" && after.Sectors[1].Text == "0,0 s");

            // --- equal lap with different splits: nothing rewritten, refs stay with the stored lap
            PersonalBests eqb = new PersonalBests(_track.Layout, new MemoryBestsStore());
            eqb.CommitValidLap(53.1f, Timing(18.4f, 17.3f, 17.4f));
            eqb.CommitValidLap(53.1f, Timing(10f, 20f, 23.1f));
            eqb.BeginAttempt();
            Check("equal lap does not replace the checkpoint refs",
                  Mathf.Abs(eqb.BaselineCheckpoint(0) - 18.4f) < 0.001f && Mathf.Abs(eqb.BaselineCheckpoint(1) - 35.7f) < 0.001f);

            // --- legacy save: a slower lap must not attach its splits to the old best; a faster one starts a proper set
            MemoryBestsStore legStore = new MemoryBestsStore();
            PersonalBests leg = new PersonalBests(_track.Layout, legStore);
            legStore.Set(leg.LapKey, 53.0f);
            leg.BeginAttempt();
            Check("legacy: best lap known, no refs, a slower lap's card is neutral cumulative",
                  leg.HasBaselineLap && !leg.HasBaselineCheckpoint(0) && !leg.HasBaselineCheckpoint(1)
                  && ResultsModel.Build(54f, Timing(19f, 17f, 18f), leg).Sectors[0].Text == "19,0 s");
            leg.CommitValidLap(54f, Timing(19f, 17f, 18f));
            leg.BeginAttempt();
            Check("legacy: slower lap leaves the legacy best intact and attaches NO checkpoint refs",
                  Mathf.Abs(leg.BaselineLap - 53f) < 0.001f && !legStore.Has(leg.CheckpointKey(0)) && !legStore.Has(leg.CheckpointKey(1))
                  && !leg.HasBaselineCheckpoint(0) && !leg.HasBaselineCheckpoint(1));
            leg.CommitValidLap(52f, Timing(18f, 17f, 17f));
            leg.BeginAttempt();
            Check("legacy: a faster lap replaces the best and writes its own refs 18,0 / 35,0",
                  Mathf.Abs(leg.BaselineLap - 52f) < 0.001f
                  && Mathf.Abs(leg.BaselineCheckpoint(0) - 18f) < 0.001f && Mathf.Abs(leg.BaselineCheckpoint(1) - 35f) < 0.001f);

            // --- invalid stored refs are rejected on load (the lap time itself stays)
            string[] badNames = { "NaN", "+inf", "-inf", "negative", "zero", "cp2 < cp1", "cp2 == cp1", "cp2 >= lap", "cp1 >= lap" };
            float[][] badRefs =
            {
                new[] { float.NaN, 37.3f }, new[] { 19.9f, float.PositiveInfinity }, new[] { float.NegativeInfinity, 37.3f },
                new[] { -19.9f, 37.3f }, new[] { 19.9f, 0f }, new[] { 30f, 20f }, new[] { 20f, 20f },
                new[] { 19.9f, 53.6f }, new[] { 60f, 70f },
            };
            for (int i = 0; i < badRefs.Length; i++)
            {
                MemoryBestsStore bs = new MemoryBestsStore();
                PersonalBests bb = new PersonalBests(_track.Layout, bs);
                bs.Set(bb.LapKey, 53.6f);
                bs.Set(bb.CheckpointKey(0), badRefs[i][0]);
                bs.Set(bb.CheckpointKey(1), badRefs[i][1]);
                bb.BeginAttempt();
                ResultsModel bm = ResultsModel.Build(53.1f, Timing(18.4f, 17.3f, 17.4f), bb);
                Check("invalid stored refs (" + badNames[i] + ") rejected: lap kept, neutral cumulative, no delta",
                      bb.HasBaselineLap && !bb.HasBaselineCheckpoint(0) && !bb.HasBaselineCheckpoint(1)
                      && bm.Sectors[0].Text == "18,4 s" && bm.Sectors[1].Text == "35,7 s" && bm.Sectors[0].Tone == ResultTone.Neutral);
            }
            {
                MemoryBestsStore bs = new MemoryBestsStore();
                PersonalBests bb = new PersonalBests(_track.Layout, bs);
                bs.Set(bb.LapKey, float.NaN); bb.BeginAttempt();
                bool nanAbsent = !bb.HasBaselineLap;
                bs.Set(bb.LapKey, float.PositiveInfinity); bb.BeginAttempt();
                Check("NaN / infinite stored best lap is treated as absent", nanAbsent && !bb.HasBaselineLap);
            }

            // --- invalid current timing -> honest '--', never a fabricated cumulative or delta
            ResultsModel inv1 = ResultsModel.Build(53.1f, new[] { 18.4f, 0f, 17.4f }, prev);
            ResultsModel inv2 = ResultsModel.Build(53.1f, new[] { 18.4f, -3f, 17.4f }, prev);
            ResultsModel inv3 = ResultsModel.Build(53.1f, new[] { 18.4f, float.PositiveInfinity, 17.4f }, prev);
            ResultsModel inv4 = ResultsModel.Build(30f, new[] { 18.4f, 17.3f, 17.4f }, prev);           // cp2 (35,7) >= lap (30)
            ResultsModel inv5 = ResultsModel.Build(float.NaN, new[] { 18.4f, 17.3f, 17.4f }, prev);
            ResultsModel inv6 = ResultsModel.Build(53.0f, new[] { 18.4f, 34.7f, 0.0f }, Bests(0f));     // cp2 (53,1) >= lap (53,0)
            Check("zero / negative / infinite split -> '--' on that and later rows (row 1 still valid)",
                  inv1.Sectors[0].Text == "+1,5 s" && inv1.Sectors[1].Text == "--" && inv2.Sectors[1].Text == "--"
                  && inv3.Sectors[1].Text == "--" && inv3.Sectors[1].Tone == ResultTone.Neutral);
            Check("cp2 at or past the lap time -> '--' (cp1 kept); NaN lap -> both '--' and no best difference",
                  inv4.Sectors[0].Text == "+1,5 s" && inv4.Sectors[1].Text == "--" && inv6.Sectors[1].Text == "--"
                  && inv5.Sectors[0].Text == "--" && inv5.Sectors[1].Text == "--" && !inv5.BestDiff.Present);

            // --- an invalid lap must store nothing
            MemoryBestsStore rjStore = new MemoryBestsStore();
            PersonalBests rj = new PersonalBests(_track.Layout, rjStore);
            bool anyWrite = rj.CommitValidLap(float.NaN, Timing(18.4f, 17.3f, 17.4f))
                         | rj.CommitValidLap(float.PositiveInfinity, Timing(18.4f, 17.3f, 17.4f))
                         | rj.CommitValidLap(0f, Timing(18.4f, 17.3f, 17.4f))
                         | rj.CommitValidLap(-5f, Timing(18.4f, 17.3f, 17.4f))
                         | rj.CommitValidLap(53.1f, null)
                         | rj.CommitValidLap(53.1f, Timing(18.4f, 17.3f))                          // a gate never closed
                         | rj.CommitValidLap(53.1f, Timing(18.4f, float.NaN, 17.4f))
                         | rj.CommitValidLap(30f, Timing(18.4f, 17.3f, 17.4f));                   // cp2 >= lap
            Check("NaN / infinite / zero / negative lap, null / incomplete / non-finite splits, cp2 >= lap: nothing stored",
                  !anyWrite && !rjStore.Has(rj.LapKey) && !rjStore.Has(rj.CheckpointKey(0)) && !rjStore.Has(rj.CheckpointKey(1))
                  && !rjStore.Has(rj.SectorKey(0)) && !rjStore.Has(rj.SectorKey(1)) && !rjStore.Has(rj.SectorKey(2)));

            // --- a teleported lap (a jump to the line): the session breaks continuity and closes at most the
            //     first gate, so even if the UI ever offered it nothing could be stored.
            RaceSession tele = new RaceSession(_track, 3000f);
            tele.Begin(); tele.StartRacing();
            tele.Tick(1f / 60f, _track.Length + 1f);
            Check("teleported lap: ContinuityBroken, not all sectors closed, commit stores nothing",
                  tele.ContinuityBroken && tele.Sectors.SectorsCompleted < tele.Sectors.SectorCount
                  && !rj.CommitValidLap(tele.ElapsedSeconds, tele.Sectors) && !rjStore.Has(rj.LapKey));

            // --- restart mid-race: splits cleared, snapshot re-read, nothing leaks into the next attempt
            PersonalBests rs = new PersonalBests(_track.Layout, new MemoryBestsStore());
            rs.CommitValidLap(53.6f, Timing(19.9f, 17.4f, 16.3f));
            rs.BeginAttempt();
            RaceSession mid = new RaceSession(_track, 3000f);
            mid.Begin(); mid.StartRacing();
            mid.Tick(1f / 60f, mid.Sectors.GateZ(0) + 1f);                                       // sector 1 closed, then the player restarts
            Check("mid-race: one sector closed", mid.Sectors.SectorsCompleted == 1 && mid.Sectors.Split(0) > 0f);
            mid.Begin(); rs.BeginAttempt();
            Check("restart: splits, completion and crossing state fully reset",
                  mid.Sectors.SectorsCompleted == 0 && mid.Sectors.Split(0) == 0f && mid.Sectors.CrossCount == 0
                  && mid.Sectors.LastClosedSector == -1 && !mid.ContinuityBroken && !mid.Finished);
            ResultsModel rsm = ResultsModel.Build(53.1f, mid.Sectors, rs);
            Check("restart: an unfinished attempt's card shows '--', not stale numbers", rsm.Sectors[0].Text == "--" && rsm.Sectors[1].Text == "--");
            Check("restart commits nothing; stored best and refs untouched",
                  !rs.CommitValidLap(1f, mid.Sectors)
                  && Mathf.Abs(rs.BaselineLap - 53.6f) < 0.001f && Mathf.Abs(rs.BaselineCheckpoint(0) - 19.9f) < 0.001f && Mathf.Abs(rs.BaselineCheckpoint(1) - 37.3f) < 0.001f);

            // --- persistence after reload through the real PlayerPrefs store (cleared again afterwards)
            PersonalBests ppA = new PersonalBests(_track.Layout, new PlayerPrefsStore());
            ppA.ClearAll();
            bool ppWrote = ppA.CommitValidLap(53.6f, Timing(19.9f, 17.4f, 16.3f));
            PersonalBests ppB = new PersonalBests(_track.Layout, new PlayerPrefsStore());        // fresh instance: only the store can supply values
            ResultsModel ppm = ResultsModel.Build(53.1f, Timing(18.4f, 17.3f, 17.4f), ppB);
            Check("PlayerPrefs reload: lap and both refs come back; card +1,5 / +1,6 and a new best [" + ppm.Sectors[0].Text + " " + ppm.Sectors[1].Text + "]",
                  ppWrote && ppB.HasBaselineLap && Mathf.Abs(ppB.BaselineLap - 53.6f) < 0.001f
                  && ppm.Sectors[0].Text == "+1,5 s" && ppm.Sectors[1].Text == "+1,6 s" && ppm.Kind == BestKind.NewBest);
            ppB.CommitValidLap(53.1f, Timing(18.4f, 17.3f, 17.4f));
            PersonalBests ppC = new PersonalBests(_track.Layout, new PlayerPrefsStore());
            Check("PlayerPrefs reload after a new best: refs belong to the new best lap (18,4 / 35,7)",
                  Mathf.Abs(ppC.BaselineLap - 53.1f) < 0.001f && Mathf.Abs(ppC.BaselineCheckpoint(0) - 18.4f) < 0.001f && Mathf.Abs(ppC.BaselineCheckpoint(1) - 35.7f) < 0.001f);
            ppC.ClearAll();
            Check("probe left no record behind in PlayerPrefs", !new PersonalBests(_track.Layout, new PlayerPrefsStore()).HasBaselineLap);

            // --- count-up lands exactly on the final text
            ResultValue rv = ResultsModel.Signed(1.5f);
            Check("count-up ends on the exact text; starts without a sign",
                  rv.TextAt(1f) == rv.Text && rv.TextAt(0f) == "0,0 s" && rv.TextAt(0.5f) != rv.Text);
        }

        // ================================================================== B. view

        private static ResultsView NewView(Vector2 panelSize, out GameObject root, out RectTransform panel)
        {
            root = new GameObject("ProbeCanvas", typeof(RectTransform), typeof(Canvas));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            GameObject p = new GameObject("Panel_Finish", typeof(RectTransform));
            p.transform.SetParent(root.transform, false);
            panel = (RectTransform)p.transform;
            UiPrimitives.Place(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, panelSize);
            Color c = UiPrimitives.Charcoal; c.a = 0.72f;
            Image scrim = UiPrimitives.Panel(panel, "Scrim", UiPrimitives.Box, c);
            UiPrimitives.Fill(scrim.rectTransform, 0f);
            ResultsView v = ResultsView.Create(panel, scrim, null);
            Canvas.ForceUpdateCanvases();
            return v;
        }

        private static bool AtFinal(ResultsView v, ResultsModel m, bool exactButton)
        {
            bool ok = v.CardAlpha > 0.999f && v.TitleAlpha > 0.999f && v.TimeAlpha > 0.999f
                      && v.BestAlpha > 0.999f && v.ButtonAlpha > 0.999f && v.UnderlineScale > 0.999f;
            for (int i = 0; i < 4; i++) ok &= v.StripeFill(i) > 0.999f;
            for (int i = 0; i < 2; i++) ok &= v.RowAlpha(i) > 0.999f && v.RowText(i) == m.Sectors[i].Text;
            ok &= v.TimeShown == m.LapText && v.BestTimeShown == m.BestTimeText && v.BestHeadingShown == m.BestHeading;
            ok &= (m.BestDiff.Present ? v.BestDiffShown == m.BestDiff.Text : v.BestDiffShown == "");
            if (exactButton) ok &= Mathf.Abs(v.ButtonScale - 1f) < 0.0005f;
            return ok;
        }

        private static bool AtInitial(ResultsView v, ResultsModel m)
        {
            bool ok = v.CardAlpha < 0.001f && v.TitleAlpha < 0.001f && v.TimeAlpha < 0.001f
                      && v.BestAlpha < 0.001f && v.ButtonAlpha < 0.001f && v.UnderlineScale < 0.001f
                      && v.ScrimAlpha < 0.001f;
            for (int i = 0; i < 4; i++) ok &= v.StripeFill(i) < 0.001f;
            for (int i = 0; i < 2; i++) ok &= v.RowAlpha(i) < 0.001f && v.RowText(i) != m.Sectors[i].Text;
            ok &= v.TimeShown != m.LapText;
            ok &= v.Elapsed == 0f && v.IsRunning && !v.IsComplete;
            return ok;
        }

        private static void Run(ResultsView v, float seconds)
        {
            int frames = Mathf.RoundToInt(seconds * 60f);
            for (int i = 0; i < frames; i++) v.Step(1f / 60f, false);
        }

        private static void ViewTests()
        {
            PersonalBests prev = Bests(53.6f, 19.9f, 17.4f, 16.3f);
            SectorTiming split = Timing(18.4f, 17.3f, 17.4f);
            ResultsModel nb = ResultsModel.Build(53.1f, split, prev);
            ResultsModel first = ResultsModel.Build(53.1f, split, Bests(0f));

            GameObject root; RectTransform panel;
            ResultsView v = NewView(new Vector2(1280f, 720f), out root, out panel);
            Check("registered as ResultsReveal.Current", ResultsReveal.Current == (IRevealSkippable)v);

            v.Begin(nb);
            Check("t=0: every element at its initial state", AtInitial(v, nb));
            Check("scrim starts at 0, card scale 1 at full height", v.FitScale > 0.999f);

            Run(v, 0.2f);
            Check("0.2 s: scrim and card on their way, nothing past its time",
                  v.CardAlpha > 0.3f && v.RowAlpha(0) < 0.001f && v.TimeAlpha < 0.001f && v.ButtonAlpha < 0.001f
                  && v.StripeFill(0) > 0.3f && v.StripeFill(0) < 1f);
            Run(v, 0.6f);
            Check("0.8 s: first row in, second on its way, time block and button not yet",
                  v.RowAlpha(0) > 0.9f && v.RowAlpha(1) < 0.9f && v.TimeAlpha < 0.001f && v.ButtonAlpha < 0.001f);
            Run(v, 1.6f);
            Check("2.4 s: every element at its final state, exact strings (new best)", AtFinal(v, nb, false));
            Check("2.4 s: improvement green, checkpoint colours green", Near(v.BestDiffColour, SectorDeltaView.Gain)
                  && Near(v.RowColour(1), SectorDeltaView.Gain) && Near(v.RowColour(0), SectorDeltaView.Gain));
            string all = "";
            foreach (Text tx in root.GetComponentsInChildren<Text>(true)) all += tx.text + "|";
            Check("card texts: PO SEKTORJU 1/2 + KONČNI ČAS present; no SEKTOR 3 / TVOJ / SEKTOR 1",
                  all.Contains("PO SEKTORJU 1") && all.Contains("PO SEKTORJU 2") && all.Contains("KONČNI ČAS")
                  && !all.Contains("SEKTOR 3") && !all.Contains("TVOJ") && !all.Contains("|SEKTOR 1"));
            Check("count-up ended on the model's exact strings", v.RowText(0) == nb.Sectors[0].Text && v.RowText(1) == nb.Sectors[1].Text && v.TimeShown == nb.LapText);
            Run(v, 0.2f);
            Check("complete by 2.45 s: static, button back at scale 1, clock stopped at Total",
                  v.IsComplete && v.Elapsed == ResultsView.Total && AtFinal(v, nb, true));
            float e = v.Elapsed;
            v.Step(0.016f, false);
            Check("a finished reveal no longer advances", v.Elapsed == e);

            // --- skip
            v.Begin(nb);
            Run(v, 0.3f);
            Vector2 onButton = RectTransformUtility.WorldToScreenPoint(null, v.ButtonRect.position);
            Vector2 offButton = RectTransformUtility.WorldToScreenPoint(null, v.CardRect.position);
            Check("tap ON the button during the reveal restarts (not consumed)", !v.ConsumeTap(onButton) && v.IsRunning && !v.IsComplete);
            Check("tap elsewhere during the reveal is consumed and completes it at once",
                  v.ConsumeTap(offButton) && v.IsComplete && AtFinal(v, nb, true));
            Check("any tap after the reveal is complete is not consumed", !v.ConsumeTap(offButton) && !v.ConsumeTap(onButton));

            // --- replay resets to t = 0, with different content
            v.Begin(first);
            Check("re-entering resets every element to t=0 (and takes the new content)",
                  AtInitial(v, first) && v.RowText(0) != "18,4 s");
            Run(v, 2.5f);
            Check("first-lap card at final: neutral cumulative times, no difference", AtFinal(v, first, true) && v.BestDiffShown == ""
                  && v.RowText(0) == "18,4 s" && v.RowText(1) == "35,7 s" && Near(v.RowColour(0), Color.white) && Near(v.RowColour(1), Color.white));

            // --- slower lap: red deltas
            ResultsModel slow = ResultsModel.Build(53.1f, split, Bests(52.9f, 18f, 17f, 17.9f));
            v.Begin(slow);
            Run(v, 2.5f);
            Check("slower card at final: red checkpoint deltas, silver best row", AtFinal(v, slow, true)
                  && v.RowText(0) == "-0,4 s" && v.RowText(1) == "-0,7 s"
                  && Near(v.RowColour(0), SectorDeltaView.Loss) && Near(v.RowColour(1), SectorDeltaView.Loss));

            // --- gate hold and hitch clamp
            v.Begin(nb);
            Run(v, 0.5f);
            float before = v.Elapsed;
            for (int i = 0; i < 120; i++) v.Step(1f / 60f, true);
            Check("time does not advance while gate-held", v.Elapsed == before);
            v.Step(1f / 60f, false);
            Check("resumes where it was", v.Elapsed > before && v.Elapsed < before + 0.02f);
            float b2 = v.Elapsed;
            v.Step(3f, false);
            Check("a hitch advances at most 1/20 s", v.Elapsed - b2 <= ResultsView.MaxDt + 1e-5f && v.Elapsed - b2 > 0.04f);

            // --- End() and the invalid state
            v.End();
            Check("after End() (left Finished) taps are never consumed", !v.IsRunning && !v.ConsumeTap(offButton));

            UnityEngine.Object.DestroyImmediate(root);

            // --- short safe area: whole card scaled down uniformly and kept inside it
            ResultsView s = NewView(new Vector2(1412f, 560f), out root, out panel);
            s.Begin(nb);
            s.Complete();
            Canvas.ForceUpdateCanvases();
            Vector3[] pc = new Vector3[4], cc = new Vector3[4];
            panel.GetWorldCorners(pc);
            s.CardRect.GetWorldCorners(cc);
            bool inside = cc[0].x >= pc[0].x && cc[0].y >= pc[0].y && cc[2].x <= pc[2].x && cc[2].y <= pc[2].y;
            Check("560-unit-tall safe area: card scaled to " + s.FitScale.ToString("F2") + " and inside it", s.FitScale < 0.95f && inside);
            UnityEngine.Object.DestroyImmediate(root);
        }

        // ============================================================== C. captures

        private const string RoadMatPath = "Assets/Art/Road/RoadUnlit.mat";

        private struct Dev
        {
            public int w, h; public Vector4 ins;
            public Dev(int w, int h, Vector4 ins) { this.w = w; this.h = h; this.ins = ins; }
        }

        private static void Captures()
        {
            Directory.CreateDirectory("Logs/results-capture");
            Dev[] devs =
            {
                new Dev(1920, 1080, Vector4.zero),
                new Dev(1280, 720, Vector4.zero),
                new Dev(844, 390, new Vector4(0, 47, 21, 47)),
                new Dev(667, 375, Vector4.zero),
            };
            SectorTiming split = Timing(18.4f, 17.3f, 17.4f);
            ResultsModel newBest = ResultsModel.Build(53.1f, split, Bests(54.6f, 19.9f, 19.6f, 15.1f));
            ResultsModel slower = ResultsModel.Build(53.1f, split, Bests(52.9f, 17.4f, 18.8f, 16.7f));
            ResultsModel firstLap = ResultsModel.Build(53.1f, split, Bests(0f));

            int seg = 40;
            foreach (Dev d in devs)
            {
                Shot(d, newBest, "newbest", -1f, seg);
                Shot(d, slower, "slower", -1f, seg);
                Shot(d, firstLap, "first", -1f, seg);
                if (d.w == 1280 || d.w == 844)
                    foreach (float t in new[] { 0.2f, 0.8f, 1.5f })
                        Shot(d, newBest, "newbest-t" + t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), t, seg);
            }
            Check("captures written to Logs/results-capture", Directory.GetFiles("Logs/results-capture", "*.png").Length >= 18);
        }

        private static void Shot(Dev d, ResultsModel model, string tag, float at, int seg)
        {
            int w = d.w, h = d.h;
            RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);

            GameObject camGo = new GameObject("CapCam"); camGo.tag = "MainCamera";
            Camera cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = 5f; cam.aspect = (float)w / h;
            cam.transform.position = new Vector3(0, 0, -10); cam.nearClipPlane = 0.3f; cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.28f, 0.6f, 0.86f);
            cam.targetTexture = rt;
            GameObject roadGo = new GameObject("CapRoad", typeof(MeshFilter), typeof(MeshRenderer));
            RoadRenderer road = roadGo.AddComponent<RoadRenderer>();
            road.Initialise(_track, AssetDatabase.LoadAssetAtPath<Material>(RoadMatPath));
            road.CameraZ = seg * 200f; road.CameraX = 0f; road.Rebuild((float)w / h);
            cam.Render();

            GameObject ucGo = new GameObject("UiCam");
            Camera uiCam = ucGo.AddComponent<Camera>();
            uiCam.orthographic = true; uiCam.orthographicSize = 5; uiCam.nearClipPlane = 0.1f; uiCam.farClipPlane = 10f;
            uiCam.transform.position = new Vector3(5000, 5000, -5);
            uiCam.clearFlags = CameraClearFlags.Depth; uiCam.targetTexture = rt; uiCam.aspect = (float)w / h;

            GameObject cvGo = new GameObject("CapCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            Canvas canvas = cvGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = uiCam; canvas.planeDistance = 1f;
            CanvasScaler cs = cvGo.GetComponent<CanvasScaler>();
            cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1280, 720);
            cs.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            cs.matchWidthOrHeight = 0.5f;
            cs.referencePixelsPerUnit = 100;
            Canvas.ForceUpdateCanvases();
            float scale = canvas.scaleFactor;

            // The safe-area host, then the same panel GameUi builds: a full-rect object with a scrim child.
            GameObject hostGo = new GameObject("SafeArea", typeof(RectTransform));
            hostGo.transform.SetParent(canvas.transform, false);
            RectTransform host = (RectTransform)hostGo.transform;
            host.anchorMin = Vector2.zero; host.anchorMax = Vector2.one;
            host.offsetMin = new Vector2(d.ins.w, d.ins.z) / scale; host.offsetMax = new Vector2(-d.ins.y, -d.ins.x) / scale;
            GameObject panelGo = new GameObject("Panel_Finish", typeof(RectTransform));
            panelGo.transform.SetParent(host, false);
            UiPrimitives.Fill((RectTransform)panelGo.transform, 0f);
            Color sc = UiPrimitives.Charcoal; sc.a = 0.72f;
            Image scrim = UiPrimitives.Panel(panelGo.transform, "Scrim", UiPrimitives.Box, sc);
            RectTransform sr = scrim.rectTransform;
            sr.anchorMin = new Vector2(-0.2f, -0.2f); sr.anchorMax = new Vector2(1.2f, 1.2f);
            sr.offsetMin = Vector2.zero; sr.offsetMax = Vector2.zero;

            ResultsView view = ResultsView.Create((RectTransform)panelGo.transform, scrim, null);
            Canvas.ForceUpdateCanvases();
            view.Begin(model);
            if (at < 0f) view.Complete();
            else Run(view, at);
            Canvas.ForceUpdateCanvases();

            uiCam.Render();
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes("Logs/results-capture/results-" + w + "x" + h + "-" + tag + ".png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            UnityEngine.Object.DestroyImmediate(cvGo);
            UnityEngine.Object.DestroyImmediate(ucGo);
            UnityEngine.Object.DestroyImmediate(roadGo);
            UnityEngine.Object.DestroyImmediate(camGo);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
