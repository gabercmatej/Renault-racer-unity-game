using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// WHERE THE ROAD ACTUALLY POINTS, AND WHERE IT ACTUALLY GOES.
    ///
    /// The segment list stores `curve`, which is a RATE of turning, not a heading. Three
    /// systems needed the heading itself and each one integrated its own copy: the
    /// minimap walked the segment list summing sines and cosines, the handling
    /// diagnostics re-derived it per test, and the car had no idea of it at all - which
    /// is precisely how the car came to follow the road for free.
    ///
    /// So it is integrated ONCE, here, at track build time, and everything reads this.
    ///
    /// THE ONE RELATIONSHIP THIS TYPE EXISTS TO STATE:
    ///
    ///     the road's heading changes by  curve / SegmentLength  radians per segment,
    ///     therefore by  curve / SegmentLength^2  radians per unit of z.
    ///
    /// That second form is <see cref="RoadGeometry.RoadYawPerCurveUnit"/>, and it is the
    /// curvature the car's Frenet-frame kinematics are written against.
    ///
    /// SIGN CONVENTION, spelled out once because every sign bug in this project has come
    /// from not having it written down: heading INCREASES as the road turns RIGHT, +x is
    /// to the RIGHT of the direction of travel, and a POSITIVE `curve` is a right-hander.
    /// The world axes are (x right, z forward) at heading zero, which is the start/finish
    /// line's own direction.
    ///
    /// HEADING IS NOT WRAPPED. A lap of this circuit is net +360 degrees, so
    /// <see cref="HeadingAt"/> on the second lap returns the first lap's heading plus
    /// 2*pi. That is deliberate: the car's own world heading is also unwrapped, and the
    /// difference between them - the road-relative yaw the whole handling model is
    /// written in - must not jump by a full turn when the car crosses the finish line.
    ///
    /// Build-time cost is one pass over the segment array. Runtime cost is an array
    /// lookup and a lerp. No allocation after construction.
    /// </summary>
    public sealed class TrackWorldFrame
    {
        private readonly float[] _heading;   // radians at the START of segment i
        private readonly float[] _curve;     // the segment's own curve, kept for interpolation
        private readonly float[] _worldX;    // centre line, world units, at the START of segment i
        private readonly float[] _worldZ;
        private readonly int _count;
        private readonly float _length;

        /// <summary>Net heading change over exactly one lap, radians. About 2*pi on a closed circuit.</summary>
        public float LapHeading { get; private set; }

        /// <summary>Where the lap's last segment ends, world units - the closure error against (0,0).</summary>
        public Vector2 LapEndpoint { get; private set; }

        public int Count { get { return _count; } }

        public TrackWorldFrame(Segment[] segments)
        {
            _count = segments != null ? segments.Length : 0;
            _heading = new float[Mathf.Max(1, _count)];
            _curve   = new float[Mathf.Max(1, _count)];
            _worldX  = new float[Mathf.Max(1, _count)];
            _worldZ  = new float[Mathf.Max(1, _count)];
            _length  = _count * RoadGeometry.SegmentLength;

            float h = 0f, x = 0f, z = 0f;
            for (int i = 0; i < _count; i++)
            {
                _heading[i] = h;
                _curve[i]   = segments[i].curve;
                _worldX[i]  = x;
                _worldZ[i]  = z;

                // Each segment is walked as a circular arc of constant heading rate, so a
                // long corner does not accumulate the error a straight-line sum makes.
                // A zero heading rate degenerates to a straight, exactly.
                float dh = segments[i].curve / RoadGeometry.SegmentLength;
                const float L = RoadGeometry.SegmentLength;
                if (Mathf.Abs(dh) < 1e-7f)
                {
                    x += Mathf.Sin(h) * L;
                    z += Mathf.Cos(h) * L;
                }
                else
                {
                    float r = L / dh;
                    x += r * (Mathf.Cos(h) - Mathf.Cos(h + dh));
                    z += r * (Mathf.Sin(h + dh) - Mathf.Sin(h));
                }
                h += dh;
            }

            LapHeading  = h;
            LapEndpoint = new Vector2(x, z);
        }

        private int IndexOf(float localZ)
        {
            int i = (int)(localZ / RoadGeometry.SegmentLength);
            if (i < 0) i = 0;
            if (i >= _count) i = _count - 1;
            return i;
        }

        /// <summary>
        /// The road's heading at distance <paramref name="z"/>, radians, UNWRAPPED across
        /// laps: crossing the finish line adds <see cref="LapHeading"/> rather than
        /// snapping back to zero.
        /// </summary>
        public float HeadingAt(float z)
        {
            if (_count == 0) return 0f;
            float laps  = Mathf.Floor(z / _length);
            float local = z - laps * _length;
            if (local < 0f) local = 0f;
            if (local >= _length) local = _length - 0.001f;

            int i = IndexOf(local);
            float t = (local - i * RoadGeometry.SegmentLength) / RoadGeometry.SegmentLength;
            return laps * LapHeading + _heading[i] + t * _curve[i] / RoadGeometry.SegmentLength;
        }

        /// <summary>
        /// Curvature at distance <paramref name="z"/>: radians of road heading per unit of
        /// z. This is the number the car's yaw kinematics subtract, and the only way road
        /// shape reaches the handling model at all.
        /// </summary>
        public float CurvatureAt(float z)
        {
            if (_count == 0) return 0f;
            float local = z - Mathf.Floor(z / _length) * _length;
            if (local < 0f) local = 0f;
            return _curve[IndexOf(local)] * RoadGeometry.RoadYawPerCurveUnit;
        }

        /// <summary>Heading at the start of a segment index (wrapped), radians within one lap.</summary>
        public float HeadingAtSegment(int index)
        {
            if (_count == 0) return 0f;
            int i = ((index % _count) + _count) % _count;
            return _heading[i];
        }

        /// <summary>
        /// The world position of a point <paramref name="lateralNormalised"/> half-widths
        /// to the RIGHT of the centre line at distance <paramref name="z"/>. Laps wrap, so
        /// this is a position on the circuit and not an ever-growing spiral.
        ///
        /// Used by the diagnostics and by the auto-turn regression test - the one place a
        /// question like "did the car actually travel in a straight line" can be answered
        /// without trusting the very state that is under test. The renderer does its own
        /// projection and does not need this.
        /// </summary>
        public Vector2 WorldPosition(float z, float lateralNormalised)
        {
            if (_count == 0) return Vector2.zero;
            float local = z - Mathf.Floor(z / _length) * _length;
            if (local < 0f) local = 0f;
            if (local >= _length) local = _length - 0.001f;

            int i = IndexOf(local);
            float t = (local - i * RoadGeometry.SegmentLength) / RoadGeometry.SegmentLength;

            float h  = _heading[i];
            float dh = _curve[i] / RoadGeometry.SegmentLength;

            float x = _worldX[i], zz = _worldZ[i];
            float swept = dh * t;
            if (Mathf.Abs(swept) < 1e-7f)
            {
                float L = RoadGeometry.SegmentLength * t;
                x  += Mathf.Sin(h) * L;
                zz += Mathf.Cos(h) * L;
            }
            else
            {
                float r = RoadGeometry.SegmentLength / dh;
                x  += r * (Mathf.Cos(h) - Mathf.Cos(h + swept));
                zz += r * (Mathf.Sin(h + swept) - Mathf.Sin(h));
            }

            float hHere = h + swept;
            float n = lateralNormalised * RoadGeometry.RoadHalfWidth;
            // The right normal of a heading that grows clockwise is (cos h, -sin h).
            return new Vector2(x + n * Mathf.Cos(hHere), zz - n * Mathf.Sin(hHere));
        }
    }
}
