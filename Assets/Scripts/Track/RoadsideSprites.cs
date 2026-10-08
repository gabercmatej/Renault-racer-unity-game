using System;
using UnityEngine;
using RenaultRacer.Core;

namespace RenaultRacer.Track
{
    /// <summary>
    /// Roadside dressing: pooled, depth-sorted, hard-capped (plan sections 11 and 16).
    ///
    /// Rules that keep this cheap enough for the weak tier, unchanged from Phase 5:
    ///
    /// 1. NOTHING IS INSTANTIATED DURING PLAY. The pool is built once in Awake and every
    ///    renderer is reused forever.
    /// 2. PROPS REUSE THE ROAD'S OWN PROJECTION. They read RoadRenderer's per-segment
    ///    samples, so they inherit the accumulated curve and the hill clip.
    /// 3. The visible count is capped (GameConfig, and QualityDirector per tier), filled
    ///    NEAREST first because those are the props the player can actually see.
    ///
    /// PHASE 10 - real art, and a layout that means something. Signs cannot come from a
    /// hash: a chevron has to point the way the bend actually goes and sit on its outside,
    /// and a 150/100/50 board has to be that far from a real braking point. So the layout
    /// is computed ONCE from the track data at startup (a few kilobytes: one prop slot per
    /// side per segment) and read every frame.
    ///
    /// PHASE 10d - the layout now comes from <see cref="TrackModel.Layout"/> rather than
    /// from re-reading the curve array. Three things change because of that:
    ///
    /// - **Signage direction is taken from <see cref="TurnInfo.IsLeft"/>**, the one place
    ///   the sign convention is written down, instead of from the sign of a curve value
    ///   re-derived here. The old scan also decided where a corner "began" by walking back
    ///   up the ramp; the layout simply says, so a chevron cannot end up on the wrong
    ///   corner because two bends merged under a threshold.
    /// - **The start/finish is a real event area.** Three times the grandstands, laid as a
    ///   continuous bank on a clamped window either side of the line - and nowhere else on
    ///   the circuit, which is the half of the brief that makes the line feel special.
    ///   <see cref="StandsAwayFromTheLine"/> counts the violations so it is checkable.
    /// - **Props that carry meaning are drawn first.** See the two-pass fill in LateUpdate.
    ///
    /// The tyre wall and the guardrail are NOT props and are not placed here: RoadRenderer
    /// draws them from each segment's own `barrierLeft`/`barrierRight`, so they follow the
    /// new corners automatically and there is nothing to relocate.
    ///
    /// Decorative only. Every prop stands outside the tyre wall, which is drawn where the
    /// car's body meets the collision barrier; CarSimulation never reads any of this.
    /// Spectators fill the run-off between the tyre wall and the guardrail beyond it, and
    /// vegetation starts outside the guardrail - so nothing green grows on the circuit.
    ///
    /// The outward order is the one a real circuit has, and the one tire_notes.txt.txt
    /// asks for: ROAD, tyre wall, standing spectators, guardrail, GRANDSTANDS. The stands
    /// are at the start/finish complex only, both verges, and are the one prop drawn at
    /// its own scale - see standScale.
    ///
    /// Two defects of the placeholder system fixed on the way, both found against real art:
    /// - Distance haze was a colour MULTIPLY, which darkens a texture instead of fading it.
    ///   The PropSprite shader blends toward the haze colour instead.
    /// - A prop at a segment that is only partly visible over a crest stood its base on
    ///   hidden road and floated over the hill. Props whose base is below the nearer
    ///   terrain's horizon are now not drawn.
    /// </summary>
    public sealed class RoadsideSprites : MonoBehaviour
    {
        [SerializeField] private RoadRenderer roadRenderer;
        [SerializeField] private GameConfig config;

        [Header("V1 status")]
        [Tooltip("Draw the roadside scenery.")]
        public bool spawnProps = true;

        [Header("Scale")]
        [Tooltip("Multiplier on real-world height for people, signs, flags and billboards. " +
                 "The car is drawn larger than life for readability, so life-size props " +
                 "next to it would look like toys.")]
        [Range(0.5f, 4f)] public float artScale = 2.6f;

        [Tooltip("Multiplier on real-world height for trees and bushes.")]
        [Range(0.5f, 4f)] public float vegetationScale = 1.5f;

        [Tooltip("Multiplier on real-world height for grandstands. Separate from " +
                 "artScale because a stand is 8 m to the top of its roof and a person " +
                 "is 1.65 m: the same multiplier that makes a spectator readable makes " +
                 "a stand a seven-storey building over the run-off. Held a little below " +
                 "artScale so the stands read as large structures set back behind the " +
                 "barrier rather than as scenery leaning over the circuit.")]
        [Range(0.5f, 4f)] public float standScale = 2.0f;

        [Header("Layout")]
        [Tooltip("Clearance beyond the guardrail for signs and people, road half-widths.")]
        [Range(0f, 2f)] public float clearance = 0.45f;

        [Tooltip("Clearance BEYOND THE GUARDRAIL for grandstands, road half-widths. A " +
                 "stand belongs behind the run-off, not on the verge with the standing " +
                 "crowd: far enough back that it never leans over the racing surface, " +
                 "close enough to read as part of the circuit.")]
        [Range(0f, 4f)] public float standOutset = 1.5f;

        [Tooltip("A vegetation prop is considered every Nth segment.")]
        [Range(2, 20)] public int vegetationStride = 7;

        [Tooltip("Segments between spectators within a crowd zone, PER SIDE. Both sides " +
                 "are filled, so 2 is a person every second segment on each verge.")]
        [Range(1, 8)] public int crowdStride = 2;

        [Tooltip("Curve strength at which a bend is worth standing at to watch.")]
        [Range(1f, 6f)] public float crowdCornerCurve = 2.5f;

        [Header("Signage (driven by TrackModel.Layout)")]
        [Tooltip("Peak |curve| at which a corner gets chevrons. A chevron is a WARNING: " +
                 "put one on every bend and it stops meaning anything.")]
        [Range(1f, 8f)] public float chevronCurve = 4f;

        [Tooltip("Peak |curve| at which a corner is a candidate for 150/100/50 braking " +
                 "boards, ALSO capped to 0.9x the hardest corner on the circuit so the " +
                 "rule survives whatever TrackBuilder finally authors. With the spec's " +
                 "curve values this nominates T01, T05 and T14 - the three braking zones " +
                 "the circuit spec names. Which boards actually go down is then decided by " +
                 "how much straight road there really is: a 150 board needs 150 m of " +
                 "approach or it is telling the driver something untrue.")]
        [Range(3f, 10f)] public float brakingCurve = 7f;

        [Tooltip("At most this many corners get braking boards, hardest first.")]
        [Range(1, 6)] public int maxBrakingZones = 4;

        [Header("Start/finish event area")]
        [Tooltip("Segments BEFORE the line the grandstand complex starts. About 1 m per " +
                 "segment, so 140 is a 140 m run-in.")]
        [Range(0, 400)] public int standsBeforeLine = 140;

        [Tooltip("Segments AFTER the line the complex continues. The pit straight is >=150 " +
                 "segments from the line to T01, so this must stay inside that.")]
        [Range(0, 400)] public int standsAfterLine = 146;

        [Tooltip("Segments between covered grandstands on the main (left) verge.")]
        [Range(10, 120)] public int coveredStandStride = 40;

        [Tooltip("Segments between open stands on the main (left) verge.")]
        [Range(8, 120)] public int openStandStride = 22;

        [Tooltip("Segments between open stands on the far (right) verge. Sparser: the " +
                 "sheet's only right-facing stand is the small one, and the far side of a " +
                 "start/finish is a thinner bank in any case.")]
        [Range(8, 160)] public int farStandStride = 34;

        [Tooltip("Segments ahead of the camera beyond which no roadside prop is drawn at " +
                 "all. Independent of the ROAD's draw distance on purpose: the road has to " +
                 "reach the horizon or the world ends in mid-air, while a prop at 250 " +
                 "segments is two pixels of haze that costs a full sprite. 150 is about " +
                 "30,000 units - past the far end of the longest straight on the circuit.")]
        [Range(40, 300)] public int propDrawDistance = 150;

        [Tooltip("Segments over which a prop fades up as it comes inside propDrawDistance. " +
                 "Anything released near the horizon must arrive nearly invisible, so that " +
                 "a crest revealing a hundred of them at once reveals a hundred nearly " +
                 "invisible ones. 25 segments is about a fifth of a second at racing speed.")]
        [Range(4, 80)] public int propFadeSegments = 25;

        [Tooltip("Sorting order floor. Must sit above the road mesh (order 0).")]
        public int sortingOrderBase = 10;

        [Tooltip("Sorting order ceiling. MUST stay below the player car's sorting order " +
                 "(100): a prop may never hide the player's own car (plan 11.3.1).")]
        public int sortingOrderCeiling = 90;

        public const string AtlasPath = "Environment/props_atlas";
        public const string MaterialPath = "Materials/PropSprite";

        private Transform[] _pool;
        private SpriteRenderer[] _renderers;
        private int _capacity;
        private int _activeCount;
        private int _visibleCap = int.MaxValue;

        private Sprite[] _sprites;
        private float[] _spriteMetres;

        // Which of the three art scales a sprite is drawn at. A byte per sprite rather
        // than a string compare per frame.
        private const byte KindArt = 0, KindVegetation = 1, KindStand = 2;
        private byte[] _spriteKind;

        // Whether a sprite MEANS something. Signs, flags, billboards and grandstands are
        // drawn before people and plants; see the two-pass fill in LateUpdate.
        private bool[] _spriteMatters;

        // Spectator sprite indices, gathered from the atlas rather than assumed. The
        // sheet count is art data: it went from ten people to twenty without a line of
        // this file needing to know, and a third sheet would cost nothing either.
        private int[] _people;

        // One slot per side per segment. -1 = empty.
        private short[] _leftSprite, _rightSprite;
        private float[] _leftOffset, _rightOffset;

        public int ActiveCount { get { return _activeCount; } }
        public int Capacity { get { return _capacity; } }

        /// <summary>Placements in the layout, for the debug HUD / probes.</summary>
        public int LayoutCount { get; private set; }

        /// <summary>Turns the layout was built from. 0 means the fallback scan found none.</summary>
        public int TurnCount { get; private set; }

        /// <summary>Grandstands placed. All of them belong at the start/finish.</summary>
        public int StandCount { get; private set; }

        /// <summary>
        /// Grandstands that ended up anywhere OTHER than the start/finish window. This is
        /// the "and nowhere else" half of the brief, counted rather than assumed, and it
        /// must be 0: a circuit lined with stands has no start/finish.
        /// </summary>
        public int StandsAwayFromTheLine { get; private set; }

        /// <summary>Chevrons placed, all of them from TurnInfo.IsLeft.</summary>
        public int ChevronCount { get; private set; }

        /// <summary>Corners that got 150/100/50 boards.</summary>
        public int BrakingZoneCount { get; private set; }

        public void SetVisibleCap(int cap)
        {
            _visibleCap = Mathf.Max(0, cap);
            if (_renderers == null) return;
            for (int i = _visibleCap; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].enabled) _renderers[i].enabled = false;
        }

        [Serializable] private sealed class SpriteJson { public string name; public string category; public float[] rect; public float[] pivot; public float metres; }
        [Serializable] private sealed class AtlasJson { public SpriteJson[] sprites; }

        private void Awake()
        {
            if (!spawnProps)
            {
                enabled = false;
                return;
            }

            if (!LoadAtlas())
            {
                enabled = false;
                return;
            }

            _capacity = config != null ? Mathf.Max(8, config.maxVisibleRoadsideSprites) : 80;
            Material mat = Resources.Load<Material>(MaterialPath);
            if (mat != null && roadRenderer != null) mat.SetColor("_Haze", roadRenderer.EffectiveHaze);

            _pool = new Transform[_capacity];
            _renderers = new SpriteRenderer[_capacity];
            for (int i = 0; i < _capacity; i++)
            {
                GameObject go = new GameObject("Prop" + i);
                go.transform.SetParent(transform, false);
                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                if (mat != null) sr.sharedMaterial = mat;
                sr.enabled = false;
                _pool[i] = go.transform;
                _renderers[i] = sr;
            }
        }

        private void Start()
        {
            // The track is built in TrackRuntime.Awake, so the layout is made here.
            if (roadRenderer != null && roadRenderer.Track != null) BuildLayout(roadRenderer.Track);
            Debug.Log("[RoadsideSprites] pool " + _capacity + ", layout " + LayoutCount + " props"
                      + " from " + TurnCount + " turns"
                      + "   stands " + StandCount + " (away from the line: " + StandsAwayFromTheLine + ")"
                      + "   chevrons " + ChevronCount
                      + "   braking zones " + BrakingZoneCount);
        }

        private bool LoadAtlas()
        {
            Texture2D tex = Resources.Load<Texture2D>(AtlasPath);
            TextAsset json = Resources.Load<TextAsset>(AtlasPath);
            if (tex == null || json == null)
            {
                Debug.LogWarning("[RoadsideSprites] " + AtlasPath + " missing - no scenery.");
                return false;
            }
            AtlasJson data = JsonUtility.FromJson<AtlasJson>(json.text);
            if (data == null || data.sprites == null || data.sprites.Length == 0) return false;

            _sprites = new Sprite[data.sprites.Length];
            _spriteMetres = new float[data.sprites.Length];
            _spriteKind = new byte[data.sprites.Length];
            _spriteMatters = new bool[data.sprites.Length];
            for (int i = 0; i < data.sprites.Length; i++)
            {
                SpriteJson s = data.sprites[i];
                _spriteMatters[i] = s.category == "sign" || s.category == "flag"
                                 || s.category == "stand" || s.category == "billboard";
                _sprites[i] = Sprite.Create(tex, new Rect(s.rect[0], s.rect[1], s.rect[2], s.rect[3]),
                                            new Vector2(s.pivot[0], s.pivot[1]), 100f, 0, SpriteMeshType.FullRect);
                _sprites[i].name = s.name;
                _spriteMetres[i] = s.metres;
                _spriteKind[i] = s.category == "vegetation" ? KindVegetation
                               : s.category == "stand" ? KindStand : KindArt;
            }
            _names = new string[data.sprites.Length];
            for (int i = 0; i < data.sprites.Length; i++) _names[i] = data.sprites[i].name;

            int people = 0;
            for (int i = 0; i < data.sprites.Length; i++)
                if (data.sprites[i].category == "spectator") people++;
            _people = new int[people];
            people = 0;
            for (int i = 0; i < data.sprites.Length; i++)
                if (data.sprites[i].category == "spectator") _people[people++] = i;
            return true;
        }

        private string[] _names;

        private int Find(string name)
        {
            for (int i = 0; i < _names.Length; i++) if (_names[i] == name) return i;
            return -1;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>
        /// Where every prop stands, computed ONCE at startup and then only read.
        ///
        /// **Everything that means something is derived from <see cref="TrackModel.Layout"/>,
        /// not from the shape of the road.** That is the whole point of TrackLayout
        /// existing: a chevron pointing the wrong way is worse than no chevron, because it
        /// tells the driver to commit to the wrong side of a corner they have not seen
        /// yet. <see cref="TurnInfo.IsLeft"/> is the one place the sign convention is
        /// spelled out, so it is the only thing consulted. Nothing here reads a curve
        /// value to decide a DIRECTION - curve magnitude decides only how much a corner
        /// deserves, never which way it goes.
        ///
        /// If the layout is empty (a track built before TrackBuilder filled it), the turns
        /// are scanned out of the segment array instead. Same code path afterwards, so the
        /// fallback cannot drift from the real thing.
        /// </summary>
        private void BuildLayout(TrackModel t)
        {
            int n = t.Count;
            _leftSprite = new short[n];
            _rightSprite = new short[n];
            _leftOffset = new float[n];
            _rightOffset = new float[n];
            for (int i = 0; i < n; i++) { _leftSprite[i] = -1; _rightSprite[i] = -1; }
            LayoutCount = 0;
            StandCount = 0;
            ChevronCount = 0;
            BrakingZoneCount = 0;

            float rail = roadRenderer != null ? roadRenderer.railOutset : 0.31f;
            float fence = roadRenderer != null && roadRenderer.drawFence ? roadRenderer.fenceOutset : 0f;

            int chevL = Find("sign_chevron_left"), chevR = Find("sign_chevron_right");
            int b150 = Find("sign_distance_150"), b100 = Find("sign_distance_100"), b50 = Find("sign_distance_50");
            int billboard = Find("billboard_renault");
            int flagChk = Find("flag_checkered"), flagYel = Find("flag_yellow"), flagRen = Find("flag_renault");

            TrackLayout layout = t.Layout;
            TurnInfo[] turns = (layout != null && layout.Turns != null && layout.Turns.Length > 0)
                             ? layout.Turns : ScanTurns(t);
            TurnCount = turns.Length;

            // --- the start/finish window, which several sections below need ------------
            //
            // Clamped against the layout so the complex sits where the circuit allows it,
            // whatever lengths TrackBuilder finally authors.
            //
            // FORWARD it stops short of T01's entry: that verge belongs to the chevrons
            // and the braking boards, and a grandstand standing in the middle of a braking
            // zone is the thing that makes a circuit look invented.
            //
            // BACKWARD it is allowed to wrap the final corner as far as its apex. A main
            // grandstand running round the outside of the last corner is what a real
            // start/finish looks like, and the stands sit beyond the guardrail - 2.4 road
            // half-widths out - so nothing about it crowds the racing line. Stopping at
            // the exit instead left a 38-segment run-in and a bank of four.
            int winFrom = -Mathf.Abs(standsBeforeLine), winTo = Mathf.Abs(standsAfterLine);
            if (turns.Length > 0)
            {
                winTo = Mathf.Min(winTo, turns[0].startSegment - 12);
                int lastHold = turns[turns.Length - 1].holdSegment;
                if (lastHold < n) winFrom = Mathf.Max(winFrom, lastHold - n + 12);
            }
            winTo = Mathf.Max(winTo, 0);
            winFrom = Mathf.Min(winFrom, 0);

            // === 1. CORNER SIGNAGE ====================================================
            //
            // Chevrons on every corner that can catch a driver out, on the OUTSIDE of the
            // bend, pointing the way the bend goes. A RIGHT-hander throws the car to the
            // LEFT, so its chevrons stand on the left verge and they are the right-pointing
            // sprite. Both facts come from IsLeft; neither is placed by eye.
            for (int c = 0; c < turns.Length; c++)
            {
                TurnInfo turn = turns[c];
                if (Mathf.Abs(turn.curve) < chevronCurve) continue;

                bool outsideLeft = !turn.IsLeft;
                int chev = turn.IsLeft ? chevL : chevR;
                int span = Span(turn, n);
                int stride = Mathf.Max(12, span / 6);
                for (int seg = turn.startSegment; seg < turn.startSegment + span; seg += stride)
                    if (Set(t, seg, outsideLeft, chev, rail + clearance)) ChevronCount++;
            }

            // Braking boards, hardest corners first and capped. The threshold is the
            // authored one OR nine tenths of the hardest corner on this circuit,
            // whichever is lower - so the rule still selects the right three corners if
            // the curve values move, instead of selecting none.
            float hardest = 0f;
            for (int i = 0; i < turns.Length; i++) hardest = Mathf.Max(hardest, Mathf.Abs(turns[i].curve));
            float brakingThreshold = Mathf.Min(brakingCurve, hardest * 0.9f);

            int[] order = HardestFirst(turns);
            int budget = maxBrakingZones;
            int flagTurn = -1;
            for (int k = 0; k < order.Length && budget > 0; k++)
            {
                TurnInfo turn = turns[order[k]];
                if (Mathf.Abs(turn.curve) < brakingThreshold) break;

                // One segment is one metre on the HUD's own km/h scale, so these are
                // honest distances to the turn-in - and a board only goes down if there
                // is that much genuinely straight road in front of it to stand in. The
                // back straight is shorter than the pit straight, so it gets fewer boards
                // rather than a 150 board planted inside the previous corner's exit.
                int approach = 0;
                for (int j = 1; j <= 400; j++)
                {
                    int seg = ((turn.startSegment - j) % n + n) % n;
                    if (Mathf.Abs(t[seg].curve) >= 0.3f) break;
                    approach++;
                }

                bool outsideLeft = !turn.IsLeft;
                bool any = false;
                float boardOut = rail + clearance + 0.5f;
                // The test is exactly "is there straight road where the board stands",
                // with no spare margin on top. A margin is tempting and it is arbitrary:
                // at 52-for-a-50-board the T14 hairpin, whose approach measures 50, lost
                // its only board and the lap's third braking zone went unmarked - while
                // the board it was refused would have stood on honest straight tarmac.
                if (approach >= 150) any |= Set(t, turn.startSegment - 150, outsideLeft, b150, boardOut);
                if (approach >= 100) any |= Set(t, turn.startSegment - 100, outsideLeft, b100, boardOut);
                if (approach >= 50)  any |= Set(t, turn.startSegment - 50,  outsideLeft, b50,  boardOut);
                if (!any) continue;

                BrakingZoneCount++;
                budget--;
                if (flagTurn < 0) flagTurn = order[k];
            }

            // A yellow flag on the INSIDE of the hardest braking corner - the one place a
            // marshal would stand. Inside a left-hander is the left verge.
            if (flagTurn >= 0)
                Set(t, turns[flagTurn].holdSegment, turns[flagTurn].IsLeft, flagYel, rail + clearance + 0.3f);

            // === 2. THE START / FINISH EVENT AREA =====================================
            //
            // The brief is two-sided and the second half matters as much as the first:
            // MORE stands here, and NONE anywhere else. A circuit lined with grandstands
            // has no start/finish - the line stops being a place. So every stand goes in
            // the one window above, and StandsAwayFromTheLine is computed afterwards to
            // prove that it did.
            //
            // Flags go down FIRST so the line itself is always marked, whatever the stand
            // spacing lands on.
            Set(t, 24, true, flagChk, rail + clearance);
            Set(t, 24, false, flagRen, rail + clearance);
            Set(t, -40, true, flagRen, rail + clearance);
            Set(t, -40, false, flagChk, rail + clearance);

            int standBig = Find("stand_covered");
            int standL = Find("stand_open_left"), standR = Find("stand_open_right");
            float standOff = rail + fence + standOutset;

            // Covered stands first so the large structures own their slots; the open
            // stands then fill between them. A covered stand is 8 m to the roof and is
            // drawn at standScale 2.0, so it is about 21 m wide on a 1 m segment - at a
            // 40-segment stride they overlap slightly and read as one continuous bank
            // rather than as separate buildings in a field.
            for (int d = winFrom; d <= winTo; d += Mathf.Max(10, coveredStandStride))
                if (Set(t, d, true, standBig, standOff)) StandCount++;
            for (int d = winFrom + openStandStride / 2; d <= winTo; d += Mathf.Max(8, openStandStride))
                if (Set(t, d, true, standL, standOff)) StandCount++;

            // The far verge is thinner on purpose. The sheet has exactly ONE right-facing
            // stand and the renderer cannot mirror a sprite, so putting stand_covered over
            // here would sit a grandstand facing away from the circuit. Trackside
            // billboards carry the far side instead, which is what a real event area does
            // with the side the cameras do not point at.
            for (int d = winFrom + farStandStride / 2; d <= winTo; d += Mathf.Max(8, farStandStride))
                if (Set(t, d, false, standR, standOff)) StandCount++;
            Set(t, -70, false, billboard, rail + clearance + 0.9f);
            Set(t, 70, false, billboard, rail + clearance + 0.9f);

            // === 3. BILLBOARDS AND SECTOR MARKERS =====================================
            //
            // One billboard per sector, on a real straight inside that sector, found from
            // the layout's own sector boundaries. Previously these were at hand-typed
            // segment numbers, which means they land wherever the track happens to be
            // after a rebuild - including inside a corner.
            int sectors = layout != null ? layout.SectorCount : 0;
            for (int s = 0; s < sectors; s++)
            {
                int from = layout.SectorStartSegment[s];
                int to = s + 1 < sectors ? layout.SectorStartSegment[s + 1] : n;
                if (to - from < 40) continue;
                int wanted = from + (to - from) * 2 / 5;
                int seg = NearestStraight(t, wanted, (to - from) / 3);
                if (seg >= 0) Set(t, seg, (s & 1) == 0, billboard, rail + clearance + 0.9f);
            }

            // Sector lines. The HUD reports splits against them, so a pair of flags where
            // the timing line actually is makes the split a place on the circuit rather
            // than a number that appears for no visible reason.
            for (int s = 1; s < sectors; s++)
            {
                Set(t, layout.SectorStartSegment[s], true, flagRen, rail + clearance);
                Set(t, layout.SectorStartSegment[s], false, flagRen, rail + clearance);
            }

            // === 4. SPECTATORS ========================================================
            //
            // People stand where there is something to watch: the start/finish straight,
            // and every corner worth the walk on a LOOSER test than the signs use. A
            // chevron is a warning and belongs only on a corner that can catch a driver
            // out; a crowd will gather at any real bend.
            int lastPerson = -1;
            lastPerson = Crowd(t, winFrom, winTo, crowdStride, rail, lastPerson);

            for (int c = 0; c < turns.Length; c++)
            {
                TurnInfo turn = turns[c];
                if (Mathf.Abs(turn.curve) < crowdCornerCurve) continue;
                int span = Span(turn, n);
                // Outside first: that is where a spectator sees the cars coming and where
                // the run-off is wide enough to stand.
                bool outsideLeft = !turn.IsLeft;
                lastPerson = CrowdSide(t, turn.startSegment - 20, turn.startSegment + span + 30,
                                       crowdStride, outsideLeft, rail, lastPerson);
                // The inside of a real corner too, but sparser - it is the tighter side.
                lastPerson = CrowdSide(t, turn.startSegment + 10, turn.startSegment + span + 10,
                                       crowdStride * 2, !outsideLeft, rail, lastPerson);
            }

            // === 5. VEGETATION, everywhere else and sparsely ==========================
            int spruce = Find("spruce"), fir = Find("fir"), broad = Find("broadleaf");
            int shrub = Find("shrub_flowering"), bush = Find("bush"), tuft = Find("grass_tuft");
            for (int i = 3; i < n; i += vegetationStride)
            {
                uint h = Hash((uint)i * 2654435761u);
                bool left = (h & 1u) == 0u;
                int type = (int)((h >> 1) % 10u);
                int sprite; float offset;
                float barrier = left ? Mathf.Abs(t[i].barrierLeft) : t[i].barrierRight;
                // Beyond the guardrail, which now stands out in the run-off: a tree
                // growing in front of it would look like it had been planted on the
                // circuit. Low scrub is allowed just outside it, where grass really does
                // run up to a rail.
                float beyondFence = rail + fence + 0.25f;
                if (type < 6)
                {
                    // No trees inside the event area. A forest behind the main grandstand
                    // is the one thing that stops it reading as a grandstand.
                    if (InWindow(i, n, winFrom, winTo)) continue;
                    sprite = type < 3 ? spruce : (type < 5 ? fir : broad);
                    offset = Mathf.Max(4.5f, barrier + beyondFence + 1.0f) + ((h >> 8) & 255u) / 255f * 2.5f;
                }
                else
                {
                    sprite = type < 8 ? bush : (type == 8 ? shrub : tuft);
                    offset = barrier + beyondFence + 0.2f + ((h >> 8) & 255u) / 255f * 0.8f;
                }
                Set(t, i, left, sprite, offset, true);
            }

            // === 6. PROVE THE "NOWHERE ELSE" HALF OF THE BRIEF ========================
            StandsAwayFromTheLine = 0;
            for (int i = 0; i < n; i++)
            {
                if (InWindow(i, n, winFrom, winTo)) continue;
                if (_leftSprite[i] >= 0 && _spriteKind[_leftSprite[i]] == KindStand) StandsAwayFromTheLine++;
                if (_rightSprite[i] >= 0 && _spriteKind[_rightSprite[i]] == KindStand) StandsAwayFromTheLine++;
            }
        }

        /// <summary>Segments from a turn's first entry segment to one past its exit, wrapped.</summary>
        private static int Span(TurnInfo turn, int n)
        {
            int span = turn.endSegment - turn.startSegment;
            if (span <= 0) span += n;
            return Mathf.Clamp(span, 1, n - 1);
        }

        /// <summary>Is a segment inside the signed window around the start/finish line?</summary>
        private static bool InWindow(int seg, int n, int from, int to)
        {
            int d = seg <= to ? seg : seg - n;      // signed distance from the line
            return d >= from && d <= to;
        }

        /// <summary>
        /// Turn indices sorted by descending |curve|. Fifteen items; a selection sort is
        /// the honest choice and allocates one int array, once, at startup.
        /// </summary>
        private static int[] HardestFirst(TurnInfo[] turns)
        {
            int[] order = new int[turns.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = 0; i < order.Length; i++)
                for (int j = i + 1; j < order.Length; j++)
                    if (Mathf.Abs(turns[order[j]].curve) > Mathf.Abs(turns[order[i]].curve))
                    { int tmp = order[i]; order[i] = order[j]; order[j] = tmp; }
            return order;
        }

        /// <summary>The nearest segment to <paramref name="wanted"/> that is actually straight.</summary>
        private static int NearestStraight(TrackModel t, int wanted, int radius)
        {
            int n = t.Count;
            for (int d = 0; d <= Mathf.Max(0, radius); d++)
            {
                int a = ((wanted + d) % n + n) % n;
                if (Mathf.Abs(t[a].curve) < 0.2f) return a;
                int b = ((wanted - d) % n + n) % n;
                if (Mathf.Abs(t[b].curve) < 0.2f) return b;
            }
            return -1;
        }

        /// <summary>
        /// Turns recovered from the segment array, for a track whose layout was never
        /// filled. Deliberately produces the SAME <see cref="TurnInfo"/> shape as
        /// TrackBuilder does, so everything downstream is identical - a fallback that took
        /// a different path would be a second, untested placement system.
        /// </summary>
        private static TurnInfo[] ScanTurns(TrackModel t)
        {
            int n = t.Count;
            var found = new System.Collections.Generic.List<TurnInfo>(24);
            int i = 0;
            while (i < n)
            {
                if (Mathf.Abs(t[i].curve) < 0.05f) { i++; continue; }
                int dir = t[i].curve > 0f ? 1 : -1;
                int e = i, hold = i;
                float peak = 0f, sum = 0f;
                while (e < n && Mathf.Abs(t[e].curve) > 0.05f && Mathf.Sign(t[e].curve) == dir)
                {
                    sum += t[e].curve;
                    if (Mathf.Abs(t[e].curve) > peak) { peak = Mathf.Abs(t[e].curve); hold = e; }
                    e++;
                }
                TurnInfo turn = new TurnInfo();
                turn.number = found.Count + 1;
                turn.startSegment = i;
                turn.holdSegment = hold;
                turn.endSegment = e;
                turn.curve = peak * dir;
                turn.degrees = sum / RoadGeometry.SegmentLength * Mathf.Rad2Deg;
                found.Add(turn);
                i = e;
            }
            return found.ToArray();
        }

        /// <summary>Fill BOTH verges of a stretch. The staggered half-stride keeps the
        /// two sides from lining up into rows facing each other.</summary>
        private int Crowd(TrackModel t, int from, int to, int stride, float rail, int last)
        {
            for (int i = from; i < to; i += stride) last = PlacePerson(t, i, true, rail, last);
            for (int i = from + stride / 2; i < to; i += stride) last = PlacePerson(t, i, false, rail, last);
            return last;
        }

        private int CrowdSide(TrackModel t, int from, int to, int stride, bool left, float rail, int last)
        {
            for (int i = from; i < to; i += stride) last = PlacePerson(t, i, left, rail, last);
            return last;
        }

        private int PlacePerson(TrackModel t, int seg, bool left, float rail, int last)
        {
            if (_people == null || _people.Length == 0) return last;
            uint h = Hash((uint)seg * 7919u + (left ? 0u : 104729u));
            int person = (int)(h % (uint)_people.Length);
            if (person == last) person = (person + 1) % _people.Length;
            // Two depths of crowd: the front row on the verge and a second rank a little
            // further back. A single line of people all at one distance reads as a fence
            // of cut-outs; two ranks read as a crowd, and cost the same.
            float jitter = ((h >> 8) & 255u) / 255f * 0.5f;
            float rank = ((h >> 16) & 3u) == 0u ? 0.62f : 0f;
            if (Set(t, seg, left, _people[person], rail + clearance + 0.15f + jitter + rank))
                return person;
            return last;
        }

        /// <summary>
        /// Put a prop in a segment's slot on one side. <paramref name="offsetBeyondBarrier"/>
        /// is measured outward from that side's barrier, unless <paramref name="absolute"/>.
        /// First come, first served: signs are placed before scenery and keep their slot.
        /// </summary>
        private bool Set(TrackModel t, int seg, bool left, int sprite, float offset, bool absolute = false)
        {
            if (sprite < 0) return false;
            int n = t.Count;
            seg = ((seg % n) + n) % n;
            float barrier = left ? Mathf.Abs(t[seg].barrierLeft) : t[seg].barrierRight;
            float lateral = absolute ? offset : barrier + offset;
            if (left)
            {
                if (_leftSprite[seg] >= 0) return false;
                _leftSprite[seg] = (short)sprite;
                _leftOffset[seg] = -lateral;
            }
            else
            {
                if (_rightSprite[seg] >= 0) return false;
                _rightSprite[seg] = (short)sprite;
                _rightOffset[seg] = lateral;
            }
            LayoutCount++;
            return true;
        }

        // ------------------------------------------------------------------ frame

        private void LateUpdate()
        {
            if (roadRenderer == null || _pool == null || _leftSprite == null) return;

            int used = 0;
            int samples = roadRenderer.SampleCount;
            int cap = Mathf.Min(_capacity, _visibleCap);
            float viewHalfWidth = Camera.main != null ? Camera.main.orthographicSize * Camera.main.aspect : 10f;
            float anisotropy = 1f / Mathf.Max(0.1f, roadRenderer.referenceAspect);

            // TWO PASSES, nearest first within each.
            //
            // The single nearest-first pass this used to be spends the cap on whatever is
            // closest, which on the grandstand straight means a grass tuft ten metres away
            // can push a grandstand - or a chevron - off the screen. That is fine for
            // scenery and wrong for anything that carries meaning: a corner sign that gets
            // culled is the same as a corner sign that lies.
            //
            // So signs, flags, billboards and stands are placed first, up to two thirds of
            // the cap, and people and plants fill what is left. The reservation matters on
            // the Weak tier, where the cap is 24 and the start/finish alone has more than
            // that many stands: without it the event area would be buildings and no crowd.
            int priorityCap = Mathf.Max(1, cap * 2 / 3);
            int firstPassLimit = Mathf.Min(cap, priorityCap);

            for (int pass = 0; pass < 2; pass++)
            {
                int limit = pass == 0 ? firstPassLimit : cap;
                for (int i = 0; i < samples && used < limit; i++)
                {
                    RoadRenderer.SegmentSample s = roadRenderer.GetSample(i);
                    int seg = s.index;
                    if (seg < 0 || seg >= _leftSprite.Length) continue;

                    // NOTHING HERE READS `i` OR `samples` ANY MORE, and that is the fix.
                    //
                    // Both of those describe the VISIBLE LIST, not the world: a crest hides
                    // everything beyond it, so the list is short while the camera climbs
                    // and long the moment it clears the summit. Between T07 and T08 the
                    // count went from a few dozen to a few hundred in one frame, and every
                    // prop's fade and sorting order were functions of it - so props already
                    // on screen snapped to full opacity and a batch of distant ones arrived
                    // at full opacity out of nothing. Depth does not move when the horizon
                    // does, so presentation reads depth.
                    if (s.depth > propDrawDistance) continue;
                    float fade = Mathf.Clamp01((propDrawDistance - s.depth)
                                               / Mathf.Max(1f, propFadeSegments));
                    // Depth-interleaved with the tyre wall buckets (RoadRenderer.PropSortingOrder).
                    int sorting = roadRenderer.PropSortingOrder(s.depth);
                    int l = _leftSprite[seg], r = _rightSprite[seg];
                    if (l >= 0 && _spriteMatters[l] == (pass == 0) && used < limit)
                        used += Place(used, s, _leftOffset[seg], l, fade, viewHalfWidth, anisotropy, sorting);
                    if (r >= 0 && _spriteMatters[r] == (pass == 0) && used < limit)
                        used += Place(used, s, _rightOffset[seg], r, fade, viewHalfWidth, anisotropy, sorting);
                }
            }

            for (int i = used; i < _capacity; i++)
                if (_renderers[i].enabled) _renderers[i].enabled = false;

            _activeCount = used;
        }

        private int Place(int slot, RoadRenderer.SegmentSample s, float offset, int sprite, float fade,
                          float viewHalfWidth, float anisotropy, int sortingOrder)
        {
            float unit = s.halfWidth;          // world units per road half-width at this depth
            if (unit <= 0.0001f) return 0;

            Sprite sp = _sprites[sprite];
            byte kind = _spriteKind[sprite];
            float scale = kind == KindVegetation ? vegetationScale
                        : kind == KindStand ? standScale : artScale;
            float heightUnits = _spriteMetres[sprite] * RoadGeometry.SegmentLength * scale;
            // Vertical size uses the projection's VERTICAL scale, exactly like hills do.
            float screenHeight = unit / roadRenderer.roadHalfWidth * heightUnits * anisotropy;
            float spriteHeight = sp.rect.height / sp.pixelsPerUnit;
            float k = screenHeight / spriteHeight;

            // Behind a nearer crest: drawn only if its TOP clears the crest line, and then
            // cut off at that line by the shader (see RoadRenderer's note on `hidden`).
            // Culling it whole, as this used to, is what made tall trees pop in against
            // the sky between T06 and T07.
            bool clipped = s.worldY < s.clipY - 0.001f;
            if (clipped && s.worldY + screenHeight <= s.clipY) return 0;

            float x = s.worldX + offset * unit;
            float halfW = sp.rect.width / sp.pixelsPerUnit * k * 0.5f;
            if (x + halfW < -viewHalfWidth || x - halfW > viewHalfWidth) return 0;

            Transform tr = _pool[slot];
            SpriteRenderer sr = _renderers[slot];
            if (sr.sprite != sp) sr.sprite = sp;
            tr.localPosition = new Vector3(x, s.worldY, 0f);
            tr.localScale = new Vector3(k, k, 1f);
            // r carries fog, a carries the draw-distance fade (see PropSprite.shader).
            float fog = Mathf.Clamp01(s.fog);
            sr.color = CrestClip.Color(fog, fade, clipped ? s.clipY : float.NegativeInfinity);
            sr.sortingOrder = sortingOrder;
            if (!sr.enabled) sr.enabled = true;
            return 1;
        }

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
    }
}
