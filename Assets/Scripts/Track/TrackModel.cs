using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// The runtime track: a flat array of segments plus wrap-safe lookups.
    /// Gameplay truth, entirely free of visual data (plan section 11).
    /// Built by <see cref="TrackBuilder"/>; never allocates after construction.
    /// </summary>
    public sealed class TrackModel
    {
        private readonly Segment[] _segments;

        public TrackModel(Segment[] segments) : this(segments, null) { }

        public TrackModel(Segment[] segments, TrackLayout layout)
        {
            _segments = segments;
            Length = segments.Length * RoadGeometry.SegmentLength;
            Layout = layout ?? new TrackLayout(null, null, segments.Length);
            WorldFrame = new TrackWorldFrame(segments);
        }

        /// <summary>
        /// The road's integrated heading and world centre line - built once, here, so
        /// every system that needs to know which way the road POINTS reads one answer.
        ///
        /// The car needs it because its heading is now its own, in world terms, and the
        /// road-relative yaw the handling model works in is the DIFFERENCE between the
        /// two. Before this existed there was no such difference to take: yaw was
        /// measured against a frame that rotated with the road, which is what made the
        /// road turn the car for the player.
        /// </summary>
        public TrackWorldFrame WorldFrame { get; private set; }

        /// <summary>Total track length in world units (one full lap).</summary>
        public float Length { get; private set; }

        /// <summary>
        /// The authored corners and sector lines. Never null - a track built without a
        /// layout reports no turns and a single sector, so a caller never has to guard.
        /// </summary>
        public TrackLayout Layout { get; private set; }

        public int Count { get { return _segments.Length; } }

        /// <summary>Direct indexed access. Index is wrapped.</summary>
        public Segment this[int index]
        {
            get { return _segments[Wrap(index)]; }
        }

        /// <summary>Wrap a segment index into range, handling negatives.</summary>
        public int Wrap(int index)
        {
            int n = _segments.Length;
            index %= n;
            if (index < 0) index += n;
            return index;
        }

        /// <summary>Wrap a distance along the track into [0, Length).</summary>
        public float WrapZ(float z)
        {
            z %= Length;
            if (z < 0f) z += Length;
            return z;
        }

        /// <summary>Index of the segment containing distance z.</summary>
        public int SegmentIndexAt(float z)
        {
            return Wrap((int)(WrapZ(z) / RoadGeometry.SegmentLength));
        }

        /// <summary>How far through its segment z sits, 0..1.</summary>
        public float SegmentPercentAt(float z)
        {
            float wrapped = WrapZ(z);
            return (wrapped % RoadGeometry.SegmentLength) / RoadGeometry.SegmentLength;
        }

        /// <summary>Interpolated hill height at distance z - the road surface under the car.</summary>
        public float HeightAt(float z)
        {
            int i = SegmentIndexAt(z);
            float t = SegmentPercentAt(z);
            return RoadGeometry.Interpolate(this[i].y, this[i + 1].y, t);
        }

        /// <summary>
        /// The LEFT collision limit at distance z, normalised (negative). Linear across the
        /// segment, from this segment's value to the next one's - which is exactly the line
        /// RoadRenderer draws the tyre wall along. Collision reads this, not the segment's
        /// own value, so the wall the car hits is the wall the player sees at every z,
        /// not just at segment starts.
        /// </summary>
        public float BarrierLeftAt(float z)
        {
            int i = SegmentIndexAt(z);
            return RoadGeometry.Interpolate(this[i].barrierLeft, this[i + 1].barrierLeft, SegmentPercentAt(z));
        }

        /// <summary>The RIGHT collision limit at distance z. See <see cref="BarrierLeftAt"/>.</summary>
        public float BarrierRightAt(float z)
        {
            int i = SegmentIndexAt(z);
            return RoadGeometry.Interpolate(this[i].barrierRight, this[i + 1].barrierRight, SegmentPercentAt(z));
        }

        /// <summary>
        /// How fast the left / right limit moves sideways per unit of z, normalised -
        /// non-zero only where the run-off changes width. Collision uses it to judge an
        /// impact against the wall's own direction rather than the road's.
        /// </summary>
        public void BarrierSlopeAt(float z, out float left, out float right)
        {
            int i = SegmentIndexAt(z);
            Segment a = this[i], b = this[i + 1];
            left  = (b.barrierLeft  - a.barrierLeft)  / RoadGeometry.SegmentLength;
            right = (b.barrierRight - a.barrierRight) / RoadGeometry.SegmentLength;
        }

        /// <summary>Curve strength at distance z.</summary>
        public float CurveAt(float z)
        {
            return this[SegmentIndexAt(z)].curve;
        }

        /// <summary>Surface at distance z.</summary>
        public Surface SurfaceAt(float z)
        {
            return this[SegmentIndexAt(z)].surface;
        }
    }
}
