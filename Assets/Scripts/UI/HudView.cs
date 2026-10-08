using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Track;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The racing HUD (plan section 14): lap time top-centre, speed bottom-right,
    /// minimap top-right. Nothing else - there is no lap counter because there is one
    /// lap, and the minimap communicates progress instead (plan section 12).
    ///
    /// Phase 10 art: the yellow stopwatch panel carries the clock, the charcoal panel the
    /// speed, the translucent frame the minimap. Every panel is a 9-slice sprite from the
    /// UI atlas and every VALUE on them stays live - the art contains no digits, no track
    /// and no marker positions.
    ///
    /// **Speed sits bottom-RIGHT** because the brake button is bottom-left (M6), so the
    /// readout is out from under the braking thumb.
    ///
    /// Every displayed string is served from a table built once at startup, so the HUD
    /// allocates nothing while racing (plan 17.5). Values are pushed only when the
    /// DISPLAYED value changes. The UI font's default digits are tabular (prepare_ui.py),
    /// so the clock does not shuffle sideways as the digits change.
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        private Text _timeText;
        private Text _tenthText;
        private Text _speedText;
        private MinimapView _minimap;
        private RectTransform _playerMarker;
        private bool _markerRotates;
        private RectTransform _startMarker;
        private SpeedLines _speedLines;
        private SectorDeltaView _sectorDelta;

        // Checkpoint diamonds on the minimap, indexed by checkpoint id.
        private Image[] _checkpointMarks;
        private float[] _checkpointFractions;

        /// <summary>
        /// Worst centre-to-centre distance between two turn badges, UI units. Measured at
        /// build time and reported by SectorProbe: fifteen numbered corners on a 160x160
        /// map is genuinely tight, so this is the number that says whether it worked
        /// rather than a screenshot.
        /// </summary>
        public float BadgeSeparation { get; private set; }

        /// <summary>Turn badges actually placed. Zero until the layout carries turns.</summary>
        public int BadgeCount { get; private set; }

        // --- pulse state (checklist 8.3) -------------------------------------------
        private RectTransform _speedRect;
        private RectTransform _tenthRect;
        private float _checkpointFlash;             // seconds remaining
        private int _lastCheckpoints = -1;
        private bool _lastOffRoad;
        private float _lastHeading = float.NaN;

        private static readonly Color SpeedNormal  = Color.white;
        private static readonly Color SpeedOffRoad = new Color(1f, 0.62f, 0.30f, 1f);
        private static readonly Color SpeedBoost   = new Color(0.45f, 0.92f, 1f, 1f);
        private const int BoostSteps = 8;
        private int _lastSpeedTint = int.MinValue;
        private float _lastBoost;
        private float _boostPulse;
        // Charcoal on the yellow timer panel; the checkpoint flash goes to a deep green
        // that still reads on yellow (white would vanish).
        private static readonly Color TimeNormal   = new Color(17f / 255f, 19f / 255f, 23f / 255f, 1f);
        private static readonly Color TenthFlash   = new Color(0.02f, 0.42f, 0.16f, 1f);
        private static readonly Color CheckpointTodo = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color CheckpointDone = new Color(1f, 0.8f, 0f, 1f);

        // --- cached strings, so the racing path never builds one -------------------
        private const int MaxMinutes = 10;          // a lap is ~40s; 10 minutes is slack
        private const int MaxKmh = 400;
        private string[] _minSec;                   // "mm:ss", indexed minute*60 + second
        private string[] _tenths;                   // ".0" .. ".9"
        private string[] _kmh;                      // "0" .. "400"

        private int _lastSecondIndex = -1;
        private int _lastTenth = -1;
        private int _lastKmh = -1;

        /// <summary>
        /// Speed shown in km/h is a presentation choice, not a simulation one: the
        /// simulation's units are arbitrary road units. The reference game showed a
        /// 0-220 km/h readout, so top speed maps to the same ceiling.
        /// </summary>
        public const float TopSpeedKmh = 220f;

        // Layout, UI units at the 1280x720 reference.
        private static readonly Vector2 TimerPanelSize = new Vector2(260f, 72f);
        private static readonly Vector2 SpeedPanelSize = new Vector2(200f, 68f);
        // THE MINIMAP PANEL, and why it is no longer a square.
        //
        // The circuit is long and low once it is turned the way the reference draws it, so
        // a square panel spent most of itself on nothing: the drawn map used 28% of the
        // width and 58% of the height, which is why fifteen numbered corners were fighting
        // for room on a map that was mostly margin. 260x140 is the same footprint in the
        // corner of the screen, turned to the shape of the thing it is drawing, and it
        // leaves the map using close to all of it at the SAME uniform scale.
        private static readonly Vector2 MinimapPanel = new Vector2(260f, 140f);
        private const float Edge = 10f;

        // --- turn badges -----------------------------------------------------------
        // The minimap's drawable square is MinimapSize minus the marker inset on each
        // side, i.e. 140x140, so local coordinates run +-70, and MinimapView.padding of
        // 24 leaves the outline itself 92 units across. A 12-unit badge (about 24 device
        // pixels on a DPR-2 phone, which is what two digits need) held 10 outside the
        // line, allowed to drift up to 18 units from its apex while the relaxation
        // separates it from its neighbours, clears EVERY overlap on the production
        // circuit: closest pair exactly 12.0 apart, furthest badge 56 of a permitted 64,
        // furthest from its own corner 11.5. Measured, not guessed - SectorProbe test [4]
        // reports all three numbers against whatever circuit is current.
        private const float BadgeUnits = 12f;
        private const float BadgeOffset = 10f;
        private const float BadgeMaxDrift = 18f;
        private const float MinimapMarkerInset = 6f;
        private const float MinimapPadding = 8f;

        public void Build(Transform parent, TrackModel track)
        {
            BuildStringTables();

            GameObject root = new GameObject("Hud", typeof(RectTransform));
            root.transform.SetParent(parent, false);
            RectTransform rr = (RectTransform)root.transform;
            UiPrimitives.Fill(rr, 0f);

            BuildLogo(root.transform);
            BuildTimer(root.transform);
            BuildSpeed(root.transform);
            BuildMinimap(root.transform, track);
            BuildSectorDelta(root.transform, track);

            // Speed lines go in FIRST as a sibling (SpeedLines.Build moves itself to the
            // front), so they are drawn behind the readouts.
            _speedLines = root.AddComponent<SpeedLines>();
            _speedLines.Build(root.transform);
        }

        // Brand logo, top-left: the wide RENAULT RACER wordmark (about 3.5:1). Height in
        // reference units (1280x720): 52 on desktop, 80 on phones, where the whole HUD is
        // shrunk and the wordmark needs a little more to stay legible. Width follows the
        // sprite's aspect (about 182 / 280 units), so it ends near x = 230 and the timer
        // starts at x = 510. It sits under the SafeArea host like everything else, so a
        // notch/cutout inset moves it in.
        private const float LogoHeightDesktop = 52f;
        private const float LogoHeightPhone = 80f;
        // Phones only: the minimap panel is drawn this much larger (about its top-right corner,
        // so it grows down and left, away from the screen edge).
        private const float MinimapPhoneScale = 1.28f;

        /// <summary>Clear space between the minimap's right edge and the sound/pause column.</summary>
        public const float MinimapControlGap = 24f;

        /// <summary>
        /// Distance from the safe area's right edge to the minimap's RIGHT edge: the sound /
        /// pause column (one button wide, flush with the HUD margin) plus a clear gap sit
        /// between the minimap and the screen edge. The minimap is built at this inset.
        /// </summary>
        public static float MinimapRightInset()
        {
            return Edge + SoundToggleView.ButtonSize.x + MinimapControlGap;
        }

        /// <summary>
        /// Distance from the safe area's right edge to the minimap's LEFT edge, at the scale
        /// the minimap is actually drawn on this device.
        /// </summary>
        public static float MinimapLeftInset()
        {
            float scale = DeviceClass.IsPhone() ? MinimapPhoneScale : 1f;
            return MinimapRightInset() + MinimapPanel.x * scale;
        }

        /// <summary>Distance from the safe area's top edge to the minimap's bottom edge, as drawn.</summary>
        public static float MinimapBottomInset()
        {
            float scale = DeviceClass.IsPhone() ? MinimapPhoneScale : 1f;
            return Edge + MinimapPanel.y * scale;
        }

        /// <summary>Timer panel half width and the logo's right extent are layout facts the probe checks.</summary>
        public static Vector2 TimerSize { get { return TimerPanelSize; } }

        /// <summary>The HUD's margin from the safe-area edge.</summary>
        public const float EdgeMargin = Edge;

        private void BuildLogo(Transform root)
        {
            Sprite logo = BrandLogo.Sprite;
            if (logo == null) return;   // no art -> nothing, never a text stand-in
            Image img = UiPrimitives.Panel(root, "BrandLogo", logo, Color.white);
            img.raycastTarget = false;
            img.preserveAspect = true;
            float aspect = logo.rect.width / Mathf.Max(1f, logo.rect.height);
            float height = DeviceClass.IsPhone() ? LogoHeightPhone : LogoHeightDesktop;
            UiPrimitives.Place(img.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                               new Vector2(Edge, -Edge), new Vector2(height * aspect, height));
            // Subtle drop shadow only, so a white mark still reads on bright sky.
            Shadow sh = img.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.6f);
            sh.effectDistance = new Vector2(2f, -2f);
            sh.useGraphicAlpha = true;
        }

        private void BuildTimer(Transform root)
        {
            Image panel = UiPrimitives.Art(root, "TimerPanel", "hud_timer_panel", Color.white,
                                           UiPrimitives.Box, new Color(1f, 0.8f, 0f, 0.92f));
            RectTransform pr = panel.rectTransform;
            UiPrimitives.Place(pr, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                               new Vector2(0f, -Edge), TimerPanelSize);

            // Split into two labels so the tenth can change ten times a second without
            // the minutes and seconds re-laying out, and so it can be smaller - it is
            // the least important digit and the most distracting one. The pair sits
            // right of centre, clear of the stopwatch in the panel's left border.
            _timeText = UiPrimitives.Label(pr, "Time", "00:00", 40, TextAnchor.MiddleRight, TimeNormal);
            UiPrimitives.Place(_timeText.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f),
                               new Vector2(66f, 1f), new Vector2(170f, 60f));

            _tenthText = UiPrimitives.Label(pr, "Tenth", ".0", 28, TextAnchor.MiddleLeft, TimeNormal);
            _tenthRect = _tenthText.rectTransform;
            UiPrimitives.Place(_tenthRect, new Vector2(0.5f, 0.5f), new Vector2(0f, 0.5f),
                               new Vector2(67f, -4f), new Vector2(50f, 44f));
        }

        private void BuildSpeed(Transform root)
        {
            Image panel = UiPrimitives.Art(root, "SpeedPanel", "hud_speed_panel", Color.white,
                                           UiPrimitives.Box, new Color(0.07f, 0.07f, 0.09f, 0.8f));
            RectTransform pr = panel.rectTransform;
            UiPrimitives.Place(pr, new Vector2(1f, 0f), new Vector2(1f, 0f),
                               new Vector2(-Edge, Edge), SpeedPanelSize);

            _speedText = UiPrimitives.Label(pr, "Speed", "0", 44, TextAnchor.MiddleRight, SpeedNormal);
            _speedRect = _speedText.rectTransform;
            UiPrimitives.Place(_speedRect, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                               new Vector2(-64f, 1f), new Vector2(128f, 60f));

            Text unit = UiPrimitives.BodyLabel(pr, "SpeedUnit", UiCopy.SpeedUnit, 16,
                                               TextAnchor.MiddleRight, UiPrimitives.Silver);
            UiPrimitives.Place(unit.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                               new Vector2(-14f, -7f), new Vector2(50f, 24f));
        }

        private void BuildMinimap(Transform root, TrackModel track)
        {
            GameObject mapGo = new GameObject("Minimap", typeof(RectTransform));
            mapGo.transform.SetParent(root, false);
            RectTransform mapRect = (RectTransform)mapGo.transform;
            UiPrimitives.Place(mapRect, new Vector2(1f, 1f), new Vector2(1f, 1f),
                               new Vector2(-MinimapRightInset(), -Edge), MinimapPanel);
            if (DeviceClass.IsPhone()) mapRect.localScale = new Vector3(MinimapPhoneScale, MinimapPhoneScale, 1f);

            // The container carries no Graphic of its own. uGUI draws a parent before its
            // children, so frame, outline and markers are siblings in draw order.
            Image frame = UiPrimitives.Art(mapGo.transform, "MinimapFrame", "hud_minimap_frame",
                                           Color.white, UiPrimitives.Box, new Color(0f, 0f, 0f, 0.35f));
            UiPrimitives.Fill(frame.rectTransform, 0f);

            const float inset = MinimapMarkerInset;
            GameObject outlineGo = new GameObject("MinimapOutline", typeof(RectTransform));
            outlineGo.transform.SetParent(mapGo.transform, false);
            UiPrimitives.Fill((RectTransform)outlineGo.transform, inset);

            _minimap = outlineGo.AddComponent<MinimapView>();
            // White, fully opaque: the outline's OWN vertex colours now carry the art -
            // the dark body and the blue/yellow/red sector dashes - and this is the tint
            // they are multiplied by. Anything other than white desaturates the sectors.
            _minimap.color = Color.white;
            _minimap.raycastTarget = false;
            // Room for a ring of turn badges outside the line. The outline is scaled to
            // (rect - 2*padding), so this is what keeps a badge inside the frame.
            // 8, not 24. The padding used to carry the whole badge ring on its own,
            // because the map was fitted to the smaller side of a square and the corners
            // were the only spare room there was. With the panel turned to the shape of
            // the circuit the map fills it, and the badges are kept inside by the per-axis
            // clamp in MinimapBadges.Place instead - which is what that clamp is for.
            _minimap.padding = MinimapPadding;
            _minimap.lineWidth = 3f;
            _minimap.bodyWidth = 6.5f;
            _minimap.dashSpans = 2;
            _minimap.outlinePoints = 96;
            _minimap.Build(track);

            // Markers share the outline's rect, so PositionAt() lands in their space.
            GameObject markers = new GameObject("MinimapMarkers", typeof(RectTransform));
            markers.transform.SetParent(mapGo.transform, false);
            UiPrimitives.Fill((RectTransform)markers.transform, inset);

            BuildCheckpointMarks(markers.transform, track);
            BuildTurnBadges(markers.transform, track);

            // The sheet's narrow checkered BAR, not ui_atlas's waving flag: at this size
            // the line across the road has to read as a line. Rotated to lie across the
            // direction of travel at the start/finish point rather than along it.
            Sprite startSprite = MinimapArt.Get("minimap_start");
            Image start = startSprite != null
                ? UiPrimitives.Panel(markers.transform, "StartFinish", startSprite, Color.white)
                : UiPrimitives.Panel(markers.transform, "StartFinish", UiPrimitives.Box,
                                     new Color(1f, 1f, 1f, 0.9f));
            _startMarker = start.rectTransform;
            UiPrimitives.Place(_startMarker, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                               _minimap.StartPosition,
                               startSprite != null
                                   ? MinimapArt.Size("minimap_start", new Vector2(9f, 21f)) * 0.5f
                                   : new Vector2(3f, 10f));
            _startMarker.localRotation = Quaternion.Euler(0f, 0f, _minimap.StartMarkerDegrees);

            _markerRotates = UiArt.Get("hud_player_arrow") != null;
            Image player = _markerRotates
                ? UiPrimitives.Art(markers.transform, "PlayerArrow", "hud_player_arrow", Color.white,
                                   UiPrimitives.Disc, new Color(1f, 0.83f, 0.1f, 1f))
                : UiPrimitives.Art(markers.transform, "PlayerDot", "hud_player_dot", Color.white,
                                   UiPrimitives.Disc, new Color(1f, 0.83f, 0.1f, 1f));
            _playerMarker = player.rectTransform;
            UiPrimitives.Place(_playerMarker, new Vector2(0.5f, 0.5f),
                               _markerRotates ? new Vector2(0.5f, 0.45f) : new Vector2(0.5f, 0.5f),
                               Vector2.zero, _markerRotates ? new Vector2(18f, 20f) : new Vector2(10f, 10f));
        }

        /// <summary>
        /// One diamond per checkpoint, at the same lap fraction the race logic uses
        /// (segment index / segment count, see RaceSession). Tinted yellow once banked.
        /// Built once; afterwards only colours change, and only when the count changes.
        /// </summary>
        private void BuildCheckpointMarks(Transform parent, TrackModel track)
        {
            if (track == null || _minimap == null || !_minimap.Ready) return;

            int count = 0;
            for (int i = 0; i < track.Count; i++)
                if (track[i].checkpointId >= 0) count++;
            if (count == 0) return;

            _checkpointMarks = new Image[count];
            _checkpointFractions = new float[count];
            bool art = UiArt.Get("hud_checkpoint") != null;

            for (int i = 0; i < track.Count; i++)
            {
                int id = track[i].checkpointId;
                if (id < 0 || id >= count || _checkpointMarks[id] != null) continue;

                float frac = (float)i / track.Count;
                _checkpointFractions[id] = frac;
                Image mark = UiPrimitives.Art(parent, "Checkpoint" + id, "hud_checkpoint",
                                              CheckpointTodo, UiPrimitives.Box, CheckpointTodo);
                // Smaller than before, and ON the line rather than beside it. The turn
                // badges are now the map's annotation layer; the diamonds are the
                // anti-cheat gates' feedback and must not compete with them.
                UiPrimitives.Place(mark.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                   _minimap.PositionAt(frac), art ? new Vector2(8f, 9f) : new Vector2(4f, 4f));
                _checkpointMarks[id] = mark;
            }
        }

        /// <summary>
        /// The numbered corner badges, `01`-`15`.
        ///
        /// Every position comes from <see cref="TrackLayout.Turns"/> - specifically from
        /// <c>holdSegment</c>, the first segment at full curve, which is the apex and the
        /// only point on a corner a number can honestly be pinned to. Nothing here is a
        /// hand-placed fraction, so retuning the corner table moves these badges with it
        /// and a badge cannot drift away from the corner it names. A track whose layout
        /// carries no turns simply gets no badges.
        ///
        /// Built once. These never move again, and none of them is touched per frame.
        /// </summary>
        private void BuildTurnBadges(Transform parent, TrackModel track)
        {
            if (track == null || _minimap == null || !_minimap.Ready) return;

            TrackLayout layout = track.Layout;
            TurnInfo[] turns = layout.Turns;
            if (turns == null || turns.Length == 0) return;

            int n = turns.Length;
            Vector2[] on = new Vector2[n];
            Vector2[] outward = new Vector2[n];
            Vector2[] placed = new Vector2[n];

            for (int i = 0; i < n; i++)
            {
                float frac = layout.FractionOf(turns[i].holdSegment);
                on[i] = _minimap.PositionAt(frac);
                outward[i] = _minimap.OutwardAt(frac);
            }

            // Half-extent of the markers rect, less half a badge, PER AXIS - the panel is
            // no longer square, so a single number would either let a badge hang off the
            // short side or squeeze the long one for nothing.
            Vector2 clampHalf = new Vector2(
                (MinimapPanel.x - MinimapMarkerInset * 2f) * 0.5f - BadgeUnits * 0.5f,
                (MinimapPanel.y - MinimapMarkerInset * 2f) * 0.5f - BadgeUnits * 0.5f);
            BadgeSeparation = MinimapBadges.Place(on, outward, n, BadgeOffset, BadgeUnits,
                                                  BadgeMaxDrift, clampHalf, placed);
            BadgeCount = n;

            Vector2 size = new Vector2(BadgeUnits, BadgeUnits);
            for (int i = 0; i < n; i++)
            {
                Sprite badge = MinimapArt.Badge(turns[i].number);
                Image img = badge != null
                    ? UiPrimitives.Panel(parent, "Turn" + turns[i].number, badge, Color.white)
                    // Without the atlas a corner is still worth marking, but an unnumbered
                    // dot is not a badge - say so by drawing it small and dim rather than
                    // pretending the number is there.
                    : UiPrimitives.Panel(parent, "Turn" + turns[i].number, UiPrimitives.Disc,
                                         new Color(1f, 1f, 1f, 0.45f));
                UiPrimitives.Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                   placed[i], badge != null ? size : new Vector2(5f, 5f));
            }
        }

        /// <summary>
        /// The sector split pill, under the clock. Built here rather than by GameUi so it
        /// shares the HUD panel's lifetime and safe-area parent, exactly like the minimap.
        /// </summary>
        private void BuildSectorDelta(Transform root, TrackModel track)
        {
            _sectorDelta = gameObject.AddComponent<SectorDeltaView>();
            _sectorDelta.Build(root, track != null ? track.Layout.SectorCount : 1);
        }

        /// <summary>
        /// A sector line has just been crossed. Called from GameUi on the frame the
        /// session's cross counter moves, never polled per frame.
        /// <paramref name="delta"/> is seconds GAINED against the pre-attempt best.
        /// </summary>
        public void ShowSectorDelta(int sector, float delta, bool hasBaseline)
        {
            if (_sectorDelta != null) _sectorDelta.Show(sector, delta, hasBaseline);
        }

        /// <summary>
        /// Push the frame's values. Nothing here allocates: every assignment is a
        /// reference to a string built at startup, and each is skipped entirely unless
        /// the value the player can actually see has changed.
        /// </summary>
        public void Tick(float elapsedSeconds, float speedPct, float lapFraction)
        {
            Tick(elapsedSeconds, speedPct, lapFraction, false, 0);
        }

        /// <summary>
        /// The full version. <paramref name="offRoad"/> tints the speed readout amber
        /// and <paramref name="checkpoints"/> flashes the clock and fills a minimap
        /// diamond when one is banked (checklist 8.3).
        /// </summary>
        public void Tick(float elapsedSeconds, float speedPct, float lapFraction,
                         bool offRoad, int checkpoints)
        {
            Tick(elapsedSeconds, speedPct, lapFraction, offRoad, checkpoints, 0f);
        }

        /// <param name="speedPct">Speed over top speed. May exceed 1 under a lightning boost,
        /// so the readout can show more than 220.</param>
        /// <param name="boostLevel">CarSimulation.BoostLevel, 0..1: tints the readout cyan.</param>
        public void Tick(float elapsedSeconds, float speedPct, float lapFraction,
                         bool offRoad, int checkpoints, float boostLevel)
        {
            int totalTenths = Mathf.Max(0, (int)(elapsedSeconds * 10f));
            int secondIndex = Mathf.Min(totalTenths / 10, MaxMinutes * 60 - 1);
            int tenth = totalTenths % 10;

            if (secondIndex != _lastSecondIndex)
            {
                _lastSecondIndex = secondIndex;
                _timeText.text = _minSec[secondIndex];
            }
            if (tenth != _lastTenth)
            {
                _lastTenth = tenth;
                _tenthText.text = _tenths[tenth];
            }

            int kmh = Mathf.Clamp(Mathf.RoundToInt(speedPct * TopSpeedKmh), 0, MaxKmh);
            if (kmh != _lastKmh)
            {
                _lastKmh = kmh;
                _speedText.text = _kmh[kmh];
            }

            if (_minimap != null && _minimap.Ready)
            {
                _playerMarker.anchoredPosition = _minimap.PositionAt(lapFraction);
                if (_markerRotates)
                {
                    float heading = _minimap.HeadingDegreesAt(lapFraction);
                    if (!(Mathf.Abs(heading - _lastHeading) < 0.5f))
                    {
                        _lastHeading = heading;
                        _playerMarker.localRotation = Quaternion.Euler(0f, 0f, heading);
                    }
                }
            }

            // --- pulses -----------------------------------------------------------
            float dt = Time.unscaledDeltaTime;

            if (checkpoints != _lastCheckpoints)
            {
                if (checkpoints > _lastCheckpoints && _lastCheckpoints >= 0) _checkpointFlash = 0.5f;
                _lastCheckpoints = checkpoints;
                if (_checkpointMarks != null)
                    for (int i = 0; i < _checkpointMarks.Length; i++)
                        if (_checkpointMarks[i] != null)
                            _checkpointMarks[i].color = i < checkpoints ? CheckpointDone : CheckpointTodo;
            }

            // Off-road amber wins; otherwise the readout runs white -> cyan with the boost,
            // in a few steps so the colour is only written when it visibly changes.
            _lastOffRoad = offRoad;
            int tint = offRoad ? -1 : Mathf.RoundToInt(Mathf.Clamp01(boostLevel) * BoostSteps);
            if (tint != _lastSpeedTint)
            {
                _lastSpeedTint = tint;
                _speedText.color = tint < 0 ? SpeedOffRoad
                                 : Color.Lerp(SpeedNormal, SpeedBoost, tint / (float)BoostSteps);
            }
            // A pickup taken: the boost jumps to full, and the readout gives one swell.
            if (boostLevel >= 0.999f && _lastBoost < 0.999f) _boostPulse = 0.45f;
            _lastBoost = boostLevel;
            float pulse = 0f;
            if (_boostPulse > 0f)
            {
                _boostPulse -= Time.unscaledDeltaTime;
                float pk = Mathf.Clamp01(_boostPulse / 0.45f);
                pulse = 0.3f * pk * pk;
            }

            // The speed readout swells with pace. Subtle on purpose. Its pivot is the
            // right edge, so it grows into the panel rather than out of it.
            float swell = 1f + 0.08f * Mathf.Clamp01((speedPct - 0.55f) / 0.45f) + pulse;
            _speedRect.localScale = new Vector3(swell, swell, 1f);

            if (_checkpointFlash > 0f)
            {
                _checkpointFlash -= dt;
                float k = Mathf.Clamp01(_checkpointFlash / 0.5f);
                _tenthText.color = Color.Lerp(TimeNormal, TenthFlash, k);
                float s = 1f + 0.35f * k * k;
                _tenthRect.localScale = new Vector3(s, s, 1f);
                if (_checkpointFlash <= 0f)
                {
                    _tenthText.color = TimeNormal;
                    _tenthRect.localScale = Vector3.one;
                }
            }

            if (_speedLines != null) _speedLines.Tick(Mathf.Min(1f, speedPct), dt);
            // Unscaled, so the pill keeps fading over a stopped simulation - the last
            // sector's split is crossed on the same tick the lap ends.
            if (_sectorDelta != null) _sectorDelta.Tick(dt);
        }

        /// <summary>Put the HUD back to zero without going through Tick's change tests.</summary>
        public void ResetDisplay()
        {
            _lastSecondIndex = -1;
            _lastTenth = -1;
            _lastKmh = -1;
            _lastCheckpoints = -1;
            _lastOffRoad = false;
            _lastSpeedTint = int.MinValue;
            _lastBoost = 0f;
            _boostPulse = 0f;
            _lastHeading = float.NaN;
            _checkpointFlash = 0f;
            if (_speedText != null) _speedText.color = SpeedNormal;
            if (_tenthText != null)
            {
                _tenthText.color = TimeNormal;
                _tenthRect.localScale = Vector3.one;
            }
            if (_speedRect != null) _speedRect.localScale = Vector3.one;
            if (_speedLines != null) _speedLines.ResetDisplay();
            // A restart must not leave the previous attempt's split hanging over the
            // countdown - the splits themselves are cleared by RaceSession.Begin.
            if (_sectorDelta != null) _sectorDelta.ResetDisplay();
            Tick(0f, 0f, 0f, false, 0);
        }

        private void BuildStringTables()
        {
            if (_minSec != null) return;

            // Same mm:ss shape as RaceSession.FormatTime, so the running clock and the
            // finish card cannot disagree. Built by hand rather than with ToString: the
            // player's browser culture is not ours to assume.
            _minSec = new string[MaxMinutes * 60];
            for (int m = 0; m < MaxMinutes; m++)
                for (int sec = 0; sec < 60; sec++)
                    _minSec[m * 60 + sec] = (m < 10 ? "0" : "") + m
                                          + ":" + (sec < 10 ? "0" : "") + sec;

            _tenths = new string[10];
            for (int i = 0; i < 10; i++) _tenths[i] = "." + i;

            _kmh = new string[MaxKmh + 1];
            for (int i = 0; i <= MaxKmh; i++) _kmh[i] = i.ToString();
        }
    }
}
