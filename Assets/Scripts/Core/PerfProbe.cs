using UnityEngine;
using UnityEngine.Profiling;

namespace RenaultRacer.Core
{
    /// <summary>
    /// The numbers plan items 9.1-9.4 and 9.7 ask for, measured IN A BUILD.
    ///
    /// Everything here exists because the Editor cannot answer these questions. Plan 9.2
    /// records the measurement that proves it: Editor Play Mode reported 3,417 B of GC
    /// per frame with every gameplay component disabled, because the Editor allocates on
    /// the same managed heap the game does. Anything read there is the Editor's number,
    /// not the game's.
    ///
    /// So this samples in the build, over a window long enough to mean something, and
    /// reports the two shapes that matter:
    ///
    ///   FRAME TIME as a percentile, not a mean. A mean hides exactly the stutter a
    ///   player notices - a lap that averages 58 FPS and drops to 20 twice a corner is
    ///   not a 58 FPS lap. The 95th percentile is the one to argue about.
    ///
    ///   GC as bytes per frame across a window, not as a per-frame delta. A collection
    ///   during the window would make a single-frame delta negative and a naive average
    ///   meaningless, so the window is discarded if the heap shrinks and restarted.
    ///
    /// Zero allocation itself: fixed arrays, no LINQ, no strings except when the readout
    /// is actually rendered.
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public sealed class PerfProbe : MonoBehaviour
    {
        [Tooltip("Frames per measurement window. 300 is about 5 s at 60 FPS - long " +
                 "enough for a percentile to mean something, short enough to react to " +
                 "a section of track.")]
        [Range(60, 1200)] public int windowFrames = 300;

        private float[] _frameMs;
        private float[] _sorted;
        private int _index;
        private bool _filled;

        private long _heapAtWindowStart;
        private int _gcFrames;

        /// <summary>Median frame time over the window, ms.</summary>
        public float MedianMs { get; private set; }

        /// <summary>95th-percentile frame time over the window, ms. The stutter number.</summary>
        public float P95Ms { get; private set; }

        /// <summary>Worst single frame in the window, ms.</summary>
        public float WorstMs { get; private set; }

        /// <summary>Managed bytes allocated per frame over the last clean window.</summary>
        public float GcBytesPerFrame { get; private set; }

        /// <summary>True once a full window has been measured.</summary>
        public bool HasWindow { get { return _filled; } }

        /// <summary>Total managed heap right now, bytes. Drift across races is the leak test.</summary>
        public long ManagedHeapBytes { get { return System.GC.GetTotalMemory(false); } }

        /// <summary>
        /// Total memory Unity has reserved, bytes. This is the one that tracks the tab's
        /// RAM footprint (plan 19), and it is NOT the managed heap - textures, meshes and
        /// audio all live outside it.
        /// </summary>
        public long ReservedBytes { get { return Profiler.GetTotalReservedMemoryLong(); } }

        private void Awake()
        {
            _frameMs = new float[windowFrames];
            _sorted = new float[windowFrames];
            StartWindow();
        }

        private void StartWindow()
        {
            _index = 0;
            _gcFrames = 0;
            _heapAtWindowStart = System.GC.GetTotalMemory(false);
        }

        private void Update()
        {
            if (_frameMs == null) return;

            _frameMs[_index] = Time.unscaledDeltaTime * 1000f;
            _index++;
            _gcFrames++;

            if (_index < _frameMs.Length) return;

            System.Array.Copy(_frameMs, _sorted, _frameMs.Length);
            System.Array.Sort(_sorted);
            MedianMs = _sorted[_sorted.Length / 2];
            P95Ms = _sorted[Mathf.Min(_sorted.Length - 1,
                                      Mathf.RoundToInt(_sorted.Length * 0.95f))];
            WorstMs = _sorted[_sorted.Length - 1];

            long heapNow = System.GC.GetTotalMemory(false);
            long delta = heapNow - _heapAtWindowStart;
            // A collection inside the window makes the delta negative and the average
            // nonsense. Throw that window away rather than report a flattering number.
            if (delta >= 0L && _gcFrames > 0)
                GcBytesPerFrame = (float)delta / _gcFrames;

            _filled = true;
            StartWindow();
        }

        /// <summary>
        /// One line, for the debug HUD. Allocates - call it only when something is
        /// actually going to be drawn.
        /// </summary>
        public void AppendTo(System.Text.StringBuilder sb)
        {
            // Memory is readable IMMEDIATELY and always. It is the leak test (plan 9.7),
            // and a leak check that cannot be read until a 300-frame window has closed is
            // useless on the device where it matters most - a weak phone takes ten seconds
            // to fill one.
            sb.Append("mem         heap ").Append((ManagedHeapBytes / 1024f / 1024f).ToString("F1"))
              .Append(" MB   reserved ").Append((ReservedBytes / 1024f / 1024f).ToString("F1"))
              .Append(" MB").Append('\n');

            if (!_filled) { sb.Append("frame ms    sampling..."); return; }
            sb.Append("frame ms    p50 ").Append(MedianMs.ToString("F1"))
              .Append("   p95 ").Append(P95Ms.ToString("F1"))
              .Append("   worst ").Append(WorstMs.ToString("F1"))
              .Append("   (").Append(Mathf.RoundToInt(1000f / Mathf.Max(0.01f, MedianMs)))
              .Append(" fps)   gc ").Append(GcBytesPerFrame.ToString("F0")).Append(" B/frame");
        }
    }
}
