using System.Globalization;
using RenaultRacer.Track;

namespace RenaultRacer.Race
{
    /// <summary>
    /// Where a personal best is kept. An interface with two implementations, so the
    /// rules can be probed without touching the player's real prefs.
    /// </summary>
    public interface IBestsStore
    {
        bool Has(string key);
        float Get(string key, float fallback);
        void Set(string key, float value);
        void Delete(string key);
        void Flush();
    }

    /// <summary>
    /// The shipping store. Under WebGL, PlayerPrefs is IndexedDB in the player's browser -
    /// which is exactly the right weight for this: it survives a reload, it costs nothing,
    /// and it is gone if they clear site data. That is acceptable and intended. These
    /// numbers are a private "beat yourself" target, not a leaderboard: they are not
    /// authoritative, not sent anywhere, and not security-sensitive. The lap time that
    /// matters to the campaign still goes through <see cref="RaceValidator"/> and, in
    /// Phase 12, the backend.
    /// </summary>
    public sealed class PlayerPrefsStore : IBestsStore
    {
        public bool Has(string key) { return UnityEngine.PlayerPrefs.HasKey(key); }
        public float Get(string key, float fallback) { return UnityEngine.PlayerPrefs.GetFloat(key, fallback); }
        public void Set(string key, float value) { UnityEngine.PlayerPrefs.SetFloat(key, value); }
        public void Delete(string key) { UnityEngine.PlayerPrefs.DeleteKey(key); }
        public void Flush() { UnityEngine.PlayerPrefs.Save(); }
    }

    /// <summary>In-memory store for <c>SectorProbe</c>. Never ships a value anywhere.</summary>
    public sealed class MemoryBestsStore : IBestsStore
    {
        private readonly System.Collections.Generic.Dictionary<string, float> _values
            = new System.Collections.Generic.Dictionary<string, float>();

        public bool Has(string key) { return _values.ContainsKey(key); }

        public float Get(string key, float fallback)
        {
            float v;
            return _values.TryGetValue(key, out v) ? v : fallback;
        }

        public void Set(string key, float value) { _values[key] = value; }
        public void Delete(string key) { _values.Remove(key); }
        public void Flush() { }
    }

    /// <summary>
    /// The player's best valid lap and best valid sector 1/2/3, stored locally.
    ///
    /// THE ONE RULE THAT MAKES THE DELTA HONEST: a sector is always compared against the
    /// best as it stood BEFORE this attempt began. <see cref="BeginAttempt"/> takes a
    /// snapshot and every comparison reads the snapshot, never the store. Comparing live
    /// would mean sector 1 improves the best, and sector 1 of the SAME lap is then
    /// measured against itself - so a good lap silently stops being able to show a gain,
    /// and the number on screen would answer a question nobody asked.
    ///
    /// THE OTHER RULE: nothing is written until the lap has been ACCEPTED. Splits are
    /// banked by <see cref="CommitValidLap"/>, which the UI calls only on entering
    /// GameState.Finished - i.e. only when <see cref="RaceValidator"/> returned None. A
    /// rejected lap can therefore not leave a sector best behind it, which is the obvious
    /// hole: cut the first corner, set a "best" sector 1, throw the lap away.
    ///
    /// KEYS ARE TRACK-SPECIFIC. The circuit is still being tuned, and a sector time from a
    /// different track shape is not a worse or better time, it is a meaningless one. The
    /// key namespace carries a fingerprint of the layout (segment count plus every sector
    /// line), so retuning the circuit starts a clean set of bests instead of showing the
    /// player a delta against a lap they drove somewhere else.
    /// </summary>
    public sealed class PersonalBests
    {
        /// <summary>Prefix of every key this type owns. Bump on an incompatible change.</summary>
        public const string KeyPrefix = "rr.pb.v1.";

        private const string LapSuffix = ".lap";
        private const string SectorSuffix = ".s";

        private readonly IBestsStore _store;
        private readonly string _lapKey;
        private readonly string[] _sectorKey;

        // The snapshot: what the store held when this attempt started.
        private float _baselineLap;
        private bool _hasBaselineLap;
        private readonly float[] _baselineSector;
        private readonly bool[] _hasBaselineSector;

        public PersonalBests(TrackLayout layout) : this(layout, new PlayerPrefsStore()) { }

        public PersonalBests(TrackLayout layout, IBestsStore store)
        {
            _store = store;

            int sectors = (layout != null) ? layout.SectorCount : 1;
            if (sectors < 1) sectors = 1;

            string ns = KeyPrefix + Fingerprint(layout);
            _lapKey = ns + LapSuffix;
            _sectorKey = new string[sectors];
            for (int i = 0; i < sectors; i++)
            {
                // Invariant culture explicitly: these are keys that have to match across
                // sessions on a phone whose locale is the player's, not ours - the same
                // reasoning as RaceSession.FormatTime.
                _sectorKey[i] = ns + SectorSuffix + (i + 1).ToString(CultureInfo.InvariantCulture);
            }

            _baselineSector = new float[sectors];
            _hasBaselineSector = new bool[sectors];
            BeginAttempt();
        }

        /// <summary>
        /// Identifies the circuit these bests belong to: segment count and every sector
        /// line. Both come from the authored layout, so this changes exactly when the
        /// thing being timed changes.
        /// </summary>
        private static string Fingerprint(TrackLayout layout)
        {
            if (layout == null) return "nolayout";

            var sb = new System.Text.StringBuilder(32);
            sb.Append('t').Append(layout.SegmentCount.ToString(CultureInfo.InvariantCulture));
            int[] starts = layout.SectorStartSegment;
            for (int i = 1; i < starts.Length; i++)
                sb.Append('_').Append(starts[i].ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public int SectorCount { get { return _sectorKey.Length; } }

        /// <summary>The key a caller can inspect or clear. Exposed for the probe.</summary>
        public string LapKey { get { return _lapKey; } }

        public string SectorKey(int sector)
        {
            return (sector >= 0 && sector < _sectorKey.Length) ? _sectorKey[sector] : null;
        }

        /// <summary>
        /// Re-read the store and freeze it for the attempt that is about to start. Call it
        /// where <see cref="RaceSession.Begin"/> is called; it is two or four PlayerPrefs
        /// reads and happens once per attempt, never per frame.
        /// </summary>
        public void BeginAttempt()
        {
            _hasBaselineLap = Read(_lapKey, out _baselineLap);
            for (int i = 0; i < _sectorKey.Length; i++)
                _hasBaselineSector[i] = Read(_sectorKey[i], out _baselineSector[i]);
        }

        private bool Read(string key, out float value)
        {
            value = 0f;
            if (!_store.Has(key)) return false;
            float v = _store.Get(key, 0f);
            // A zero or negative best is not a fast lap, it is a corrupt or half-written
            // pref. Treat it as absent rather than as an unbeatable record.
            if (v <= 0f) return false;
            value = v;
            return true;
        }

        public bool HasBaselineLap { get { return _hasBaselineLap; } }
        public float BaselineLap { get { return _baselineLap; } }

        public bool HasBaselineSector(int sector)
        {
            return sector >= 0 && sector < _hasBaselineSector.Length && _hasBaselineSector[sector];
        }

        public float BaselineSector(int sector)
        {
            return HasBaselineSector(sector) ? _baselineSector[sector] : 0f;
        }

        /// <summary>
        /// Seconds GAINED against the pre-attempt best: positive means faster than the
        /// best, negative means slower. Only meaningful when
        /// <see cref="HasBaselineSector"/> is true - with no baseline there is no delta,
        /// and inventing one is how a first lap ends up bragging about beating nothing.
        /// </summary>
        public float SectorDelta(int sector, float split)
        {
            return HasBaselineSector(sector) ? _baselineSector[sector] - split : 0f;
        }

        /// <summary>Seconds gained against the pre-attempt best lap. Same convention.</summary>
        public float LapDelta(float lapSeconds)
        {
            return _hasBaselineLap ? _baselineLap - lapSeconds : 0f;
        }

        /// <summary>
        /// Bank an ACCEPTED lap. The caller must have validated it first - see the class
        /// note. Every completed sector plus the lap itself is written if, and only if, it
        /// beats what is stored. Returns true if anything was written.
        /// </summary>
        public bool CommitValidLap(float lapSeconds, SectorTiming sectors)
        {
            if (lapSeconds <= 0f) return false;

            // Half a set of splits is not a lap. If the gates did not all close, the lap
            // did not go the way round the sector lines say it does, and none of it is a
            // record of anything.
            if (sectors == null || sectors.SectorsCompleted != sectors.SectorCount) return false;

            bool wrote = Improve(_lapKey, lapSeconds);
            int n = sectors.SectorCount < _sectorKey.Length ? sectors.SectorCount : _sectorKey.Length;
            for (int i = 0; i < n; i++)
                wrote |= Improve(_sectorKey[i], sectors.Split(i));

            if (wrote) _store.Flush();
            return wrote;
        }

        private bool Improve(string key, float value)
        {
            if (value <= 0f) return false;
            float current;
            if (Read(key, out current) && current <= value) return false;
            _store.Set(key, value);
            return true;
        }

        /// <summary>Forget everything for this circuit, and re-snapshot.</summary>
        public void ClearAll()
        {
            _store.Delete(_lapKey);
            for (int i = 0; i < _sectorKey.Length; i++) _store.Delete(_sectorKey[i]);
            _store.Flush();
            BeginAttempt();
        }

        /// <summary>What is stored right now, ignoring the snapshot. For the probe.</summary>
        public bool StoredLap(out float value) { return Read(_lapKey, out value); }

        public bool StoredSector(int sector, out float value)
        {
            value = 0f;
            return sector >= 0 && sector < _sectorKey.Length && Read(_sectorKey[sector], out value);
        }
    }
}
