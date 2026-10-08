using UnityEngine;

namespace RenaultRacer.InputSystem
{
    /// <summary>
    /// Pure touch-control geometry: no Screen, no WebSafeArea, no state. TouchDriveInput
    /// and TouchControlsView both call this with the live screen, and the editor test
    /// (TouchLayoutProbe) calls it with fixed phone sizes - one code path, so what the
    /// test proves is what the player gets.
    /// </summary>
    public static class TouchLayout
    {
        /// <summary>What a touch that begins at a point becomes.</summary>
        public enum Zone : byte { SteerLeft, SteerRight, Brake }

        /// <summary>
        /// The brake rect (screen pixels, y up). Size and margin scale with the shorter
        /// screen edge; the inward offset scales with the SAFE width, so the button sits
        /// the same proportion of the way in on a 16:9 desktop window and a 19.5:9 phone.
        /// The left/right offset from the safe edge is max(margin, inset * safeWidth).
        /// </summary>
        public static Rect BrakeRect(int width, int height, Rect safe, bool onLeft,
                                     float sizeFrac, float marginFrac, float inwardFrac)
        {
            float shortEdge = Mathf.Min(width, height);
            float size = shortEdge * sizeFrac;
            float margin = shortEdge * marginFrac;
            float edge = Mathf.Max(margin, safe.width * inwardFrac);
            float x = onLeft ? safe.xMin + edge : safe.xMax - edge - size;
            float y = safe.yMin + margin;
            return new Rect(x, y, size, size);
        }

        /// <summary>The brake wins any touch inside it; otherwise left/right of the split.</summary>
        public static Zone Classify(Vector2 p, Rect brake, float splitX)
        {
            if (brake.Contains(p)) return Zone.Brake;
            return p.x < splitX ? Zone.SteerLeft : Zone.SteerRight;
        }

        /// <summary>Chevron centres (screen pixels) inset from the safe side edges.</summary>
        public static void ChevronCentres(int width, int height, Rect safe, float inset, float heightFrac,
                                          out Vector2 left, out Vector2 right)
        {
            float y = height * heightFrac;
            float dx = width * inset;
            left = new Vector2(safe.xMin + dx, y);
            right = new Vector2(safe.xMax - dx, y);
        }
    }
}
