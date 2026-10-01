namespace RenaultRacer.Track
{
    /// <summary>
    /// The on-road surface for a segment. Off-road (grass) is derived from the car's
    /// lateral position, not stored here - this is for special stretches such as a
    /// sand trap or a rumble-heavy section.
    /// </summary>
    public enum Surface : byte
    {
        Road = 0,
        Rumble = 1,
        Grass = 2,
        Sand = 3
    }

    /// <summary>
    /// Physical coefficients per surface (plan section 8.9). Looked up by enum rather
    /// than stored per segment - a track has thousands of segments and they nearly all
    /// share one surface.
    /// </summary>
    public static class SurfaceTable
    {
        // Indexed by (int)Surface.
        private static readonly float[] GripTable     = { 1.00f, 0.85f, 0.45f, 0.35f };
        private static readonly float[] DragTable     = { 0.00f, 0.04f, 0.25f, 0.45f };
        private static readonly float[] SpeedCapTable = { 1.00f, 0.97f, 0.60f, 0.45f };

        public static float Grip(Surface s)     { return GripTable[(int)s]; }
        public static float DragOf(Surface s)   { return DragTable[(int)s]; }
        public static float SpeedCap(Surface s) { return SpeedCapTable[(int)s]; }
    }

    /// <summary>
    /// One road segment. Gameplay truth only - no visual data (plan section 11).
    /// Kept a struct in a flat array so iteration is cache-friendly and allocation-free.
    /// </summary>
    public struct Segment
    {
        /// <summary>Lateral bend applied per segment while driving through it.</summary>
        public float curve;

        /// <summary>Hill height at the START of this segment, world units.</summary>
        public float y;

        /// <summary>On-road surface type.</summary>
        public Surface surface;

        /// <summary>Normalised lateral limits. 1 = road edge; barriers sit beyond.</summary>
        public float barrierLeft;
        public float barrierRight;

        /// <summary>True on the single segment that forms the finish gate.</summary>
        public bool isFinishGate;

        /// <summary>Validation checkpoint index, or -1 for none.</summary>
        public int checkpointId;
    }
}
