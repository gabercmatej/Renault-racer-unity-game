using UnityEngine;

namespace RenaultRacer.Track
{
    /// <summary>
    /// Pure pseudo-3D projection math. No Unity objects, no state, no allocation.
    ///
    /// Works in the reference racer's virtual-screen convention: pixels, origin
    /// top-left, +y DOWN. RoadRenderer converts to world units at the last moment.
    /// Keeping the proven convention internally avoids a whole class of sign bugs.
    /// </summary>
    public static class RoadGeometry
    {
        // Reference constants (plan section 2.1), verified against shell.final.html.
        public const float SegmentLength = 200f;
        public const int   RumbleLength  = 3;
        public const float RoadHalfWidth = 1100f;
        public const float CameraHeight  = 1000f;
        public const float FieldOfView   = 100f;

        /// <summary>
        /// Radians the road's own tangent rotates, per unit of <c>curve</c>, per unit
        /// of distance travelled along z.
        ///
        /// Derived from how the renderer lays the road out: it walks the segment list
        /// accumulating <c>x += dx; dx += curve;</c>, so one segment of travel adds
        /// <c>curve</c> world-x units to the centre line's lateral slope. The slope
        /// over one segment is <c>dx / SegmentLength</c>, which for small angles IS
        /// the heading, so heading changes by <c>curve / SegmentLength</c> per segment
        /// and by <c>curve / SegmentLength^2</c> per unit of z.
        ///
        /// CarSimulation needs this because its yaw is measured RELATIVE TO THE ROAD.
        /// A road-relative frame that rotates is not inertial, and a model that does
        /// not subtract this rotation is silently steering the car around every corner
        /// (plan 8.15).
        /// </summary>
        public const float RoadYawPerCurveUnit = 1f / (SegmentLength * SegmentLength);

        /// <summary>Projection depth from a vertical field of view, in degrees.</summary>
        public static float CameraDepth(float fovDegrees)
        {
            return 1f / Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);
        }

        /// <summary>
        /// A world point projected to the virtual screen.
        /// <paramref name="w"/> is the projected HALF width of the road at that depth.
        /// </summary>
        public struct Projected
        {
            public float x, y, w;   // virtual-screen pixels, y DOWN
            public float camZ;      // depth in front of the camera
            public float scale;     // perspective scale factor
        }

        /// <summary>
        /// Project a road-space point. <paramref name="worldX"/> is a lateral offset in
        /// world units (not normalised), <paramref name="worldY"/> the hill height,
        /// <paramref name="worldZ"/> the distance along the track.
        /// </summary>
        public static Projected Project(
            float worldX, float worldY, float worldZ,
            float camX, float camY, float camZ,
            float cameraDepth, float virtualWidth, float virtualHeight,
            float roadHalfWidth)
        {
            Projected p;
            p.camZ = worldZ - camZ;

            // Guard the near plane: never divide by ~0, and never project behind the eye.
            float safeZ = p.camZ < 0.0001f ? 0.0001f : p.camZ;
            p.scale = cameraDepth / safeZ;

            float halfW = virtualWidth * 0.5f;
            float halfH = virtualHeight * 0.5f;

            p.x = halfW + p.scale * (worldX - camX) * halfW;
            p.y = halfH - p.scale * (worldY - camY) * halfH;
            p.w =         p.scale * roadHalfWidth   * halfW;
            return p;
        }

        /// <summary>
        /// IS THIS BAND OF ROAD DRAWN? The renderer's hill-occlusion test, in one place so
        /// that a probe can replay it without keeping a second copy that drifts.
        ///
        /// Three conditions, and the third is the hill: a band is drawn when it is in
        /// front of the near plane, when it recedes (its far edge is higher up the screen
        /// than its near edge), and when its far edge is higher than everything nearer
        /// already drawn. That last one is what a crest does - it raises the running
        /// horizon and hides the road beyond until the camera climbs over it.
        /// </summary>
        public static bool BandVisible(Projected near, Projected far, float cameraDepth, float maxY)
        {
            return near.camZ > cameraDepth && far.y < near.y && far.y < maxY;
        }

        /// <summary>Linear interpolation across a segment.</summary>
        public static float Interpolate(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// Smooth ease used by the hill/curve builders. Matches the reference's
        /// easeInOut so ported track layouts keep their shape.
        /// </summary>
        public static float EaseIn(float a, float b, float t)   { return a + (b - a) * Mathf.Pow(t, 2f); }
        public static float EaseOut(float a, float b, float t)  { return a + (b - a) * (1f - Mathf.Pow(1f - t, 2f)); }
        public static float EaseInOut(float a, float b, float t){ return a + (b - a) * (-Mathf.Cos(t * Mathf.PI) * 0.5f + 0.5f); }

        /// <summary>Exponential distance fog, 0 = fully fogged, 1 = clear.</summary>
        public static float Fog(float distanceFraction, float density)
        {
            return 1f / Mathf.Pow(2.718281828f, distanceFraction * distanceFraction * density);
        }
    }
}
