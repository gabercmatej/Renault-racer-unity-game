namespace RenaultRacer.Track
{
    /// <summary>
    /// One numbered corner, as the circuit reference names it.
    ///
    /// This exists so that the minimap, the corner signage and the sector timing all
    /// describe the SAME corner the car drives through. V1 had no such record: the
    /// minimap integrated the curve list, the signs were placed by eye, and nothing
    /// tied either to a turn number - so "turn 5" meant three different pieces of road
    /// depending on which system you asked.
    /// </summary>
    public struct TurnInfo
    {
        /// <summary>1..15, matching the circuit reference.</summary>
        public int number;

        /// <summary>First segment of the entry ramp.</summary>
        public int startSegment;

        /// <summary>First segment at full curve - the apex, and where a marker belongs.</summary>
        public int holdSegment;

        /// <summary>One past the last segment of the exit ramp.</summary>
        public int endSegment;

        /// <summary>Peak curve, SIGNED. Negative is a left-hander, positive a right.</summary>
        public float curve;

        /// <summary>Signed heading change across the whole corner, degrees.</summary>
        public float degrees;

        /// <summary>True on a left-hander. The one place the sign convention is spelled out.</summary>
        public bool IsLeft { get { return curve < 0f; } }
    }

    /// <summary>
    /// The authored shape of the circuit, alongside the flat segment array that the car
    /// actually drives. <see cref="TrackBuilder"/> fills it; <see cref="TrackModel"/>
    /// carries it; the minimap, the sector timer and the roadside signage read it.
    ///
    /// THE POINT OF THIS TYPE is that there is exactly one description of where a corner
    /// or a sector line is, and every system reads that one. A minimap that draws its own
    /// idea of the track, or a sector gate placed at a hand-typed distance, is how the
    /// map and the road drift apart.
    /// </summary>
    public sealed class TrackLayout
    {
        public static readonly int[] NoSectors = { 0 };

        public TrackLayout(TurnInfo[] turns, int[] sectorStartSegment, int segmentCount)
        {
            Turns = turns ?? new TurnInfo[0];
            SectorStartSegment = (sectorStartSegment != null && sectorStartSegment.Length > 0)
                               ? sectorStartSegment : NoSectors;
            SegmentCount = segmentCount;
        }

        /// <summary>Turns 1..15 in driving order.</summary>
        public TurnInfo[] Turns { get; private set; }

        /// <summary>
        /// First segment of each sector, ascending. Element 0 is always 0 - sector 1
        /// begins at the start/finish line by definition.
        /// </summary>
        public int[] SectorStartSegment { get; private set; }

        public int SectorCount { get { return SectorStartSegment.Length; } }

        public int SegmentCount { get; private set; }

        /// <summary>0..1 position around the lap of a segment index.</summary>
        public float FractionOf(int segment)
        {
            return SegmentCount > 0 ? (float)segment / SegmentCount : 0f;
        }

        /// <summary>Distance along the track of a sector's opening line, world units.</summary>
        public float SectorStartZ(int sector)
        {
            if (sector < 0 || sector >= SectorStartSegment.Length) return 0f;
            return SectorStartSegment[sector] * RoadGeometry.SegmentLength;
        }

        /// <summary>Which sector (0-based) a segment index falls in.</summary>
        public int SectorAtSegment(int segment)
        {
            int s = 0;
            for (int i = 1; i < SectorStartSegment.Length; i++)
                if (segment >= SectorStartSegment[i]) s = i;
            return s;
        }
    }
}
