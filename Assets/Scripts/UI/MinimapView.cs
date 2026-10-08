using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using RenaultRacer.Track;

namespace RenaultRacer.UI
{
    /// <summary>
    /// The track minimap (plan section 11.1) - top-right of the racing HUD.
    ///
    /// Its job is to tell the player where they are, how much is left, and what is
    /// coming, so that a committing corner reads as fair rather than as a blind
    /// surprise (plan 11.2).
    ///
    /// **THE DRAWN PATH IS THE DRIVEN PATH.** The outline is integrated out of
    /// <see cref="TrackModel"/> - the same `curve` values the road renderer and
    /// CarSimulation consume - and nothing here is traced from an image or authored by
    /// hand. That is the whole reason this is a mesh and not a picture: a picture of a
    /// circuit is a second, independent claim about the track's shape, and the moment the
    /// circuit is retuned the picture is quietly wrong while looking completely fine. The
    /// Phase 11 art sheet's dashed strips and arcs are therefore used only as a COLOUR and
    /// STYLE reference (see Tools/ArtPrep/prepare_minimap.py); the geometry is ours.
    ///
    /// **The performance requirement is the design.** No second camera, no
    /// RenderTexture, no duplicate world, no geometry regenerated per frame. The outline
    /// is derived once, decimated, and normalised to fit the rect. After that, exactly one
    /// RectTransform moves per frame.
    ///
    /// Drawn as a single <see cref="Graphic"/>, so the dark track body AND the sector
    /// dashes over it are one UI mesh and therefore at most one draw call, not one per
    /// segment. The dashes carry their sector's colour in their VERTICES, which is what
    /// lets three colours share a mesh without three materials. That also means it needs a
    /// CanvasRenderer of its own - Image declares that dependency for you, a hand-written
    /// Graphic has to declare it itself, and without one it silently draws nothing.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MinimapView : MaskableGraphic
    {
        [Tooltip("Points kept after decimating the centre line. The plan's range is " +
                 "60-120: enough for the lap to be recognisable, few enough that the " +
                 "mesh is trivial. A multiple of 4 keeps the dash pattern continuous " +
                 "across the closing span.")]
        [Range(40, 160)] public int outlinePoints = 96;

        [Tooltip("Width of the coloured sector dash.")]
        [Range(1f, 8f)] public float lineWidth = 3f;

        [Tooltip("Width of the dark track body under the dashes. Must exceed lineWidth " +
                 "or the casing disappears.")]
        [Range(1f, 12f)] public float bodyWidth = 6.5f;

        [Tooltip("Spans per dash and per gap. 2 gives a dash roughly twice the line " +
                 "width, which is the source sheet's rhythm.")]
        [Range(1, 6)] public int dashSpans = 2;

        [Tooltip("Inset from the rect's edge, so the outline is not clipped and neither " +
                 "the player marker nor a turn badge sits half off the map.")]
        [Range(0f, 40f)] public float padding = 24f;

        private Vector2[] _shape;         // normalised to the rect, built once
        private int[] _segmentAt;         // track segment index each point came from
        private byte[] _sectorAt;         // which sector each point is in
        private int _pointCount;
        private int _sectorCount = 1;
        private Vector2 _centroid;        // normalised space, for the outward normals
        private Vector2 _extent = Vector2.one;   // the shape box, longer side = 1
        private float _closureError;      // |lap endpoint - start|, normalised units

        /// <summary>True once a track has been supplied and the outline cached.</summary>
        public bool Ready { get { return _pointCount > 1; } }

        /// <summary>Sectors the layout declares. 1 when the track has no sector lines.</summary>
        public int SectorCount { get { return _sectorCount; } }

        /// <summary>
        /// Build the outline. Called once, when the track is built - the track is static
        /// for the whole session, so this never runs again (plan 11.1).
        /// </summary>
        public void Build(TrackModel track)
        {
            if (track == null || track.Count < 4) return;

            int n = Mathf.Clamp(outlinePoints, 8, 512);
            if (_shape == null || _shape.Length != n)
            {
                _shape = new Vector2[n];
                _segmentAt = new int[n];
                _sectorAt = new byte[n];
            }

            Vector2 endpoint;
            _pointCount = MinimapGeometry.Integrate(track, n, _shape, null,
                                                    _segmentAt, out endpoint);
            if (_pointCount < 2) return;

            float scale;
            MinimapGeometry.Normalise(_shape, _pointCount, out _centroid, out scale, out _extent);

            // How far the lap misses its own start, in the SAME normalised units as the
            // shape. Measured from the full integration rather than from the last KEPT
            // point: the last point sits a stride short of the line, so comparing it with
            // the start would call a perfectly closed circuit open at low point counts.
            _closureError = endpoint.magnitude * scale;

            // Sector per point, straight from the authored layout. No distances are typed
            // here, so moving a sector line in TrackBuilder recolours this map by itself.
            TrackLayout layout = track.Layout;
            _sectorCount = Mathf.Max(1, layout.SectorCount);
            for (int i = 0; i < _pointCount; i++)
                _sectorAt[i] = (byte)Mathf.Clamp(layout.SectorAtSegment(_segmentAt[i]),
                                                 0, MinimapArt.MaxSectors - 1);

            SetVerticesDirty();
        }

        /// <summary>
        /// Where the player is, in this rect's local space. One array lookup and a
        /// lerp - the per-frame cost of the whole minimap (plan 11.1).
        /// </summary>
        public Vector2 PositionAt(float lapFraction)
        {
            if (!Ready) return Vector2.zero;
            return Scaled(Normalised(lapFraction));
        }

        private Vector2 Normalised(float lapFraction)
        {
            return MinimapGeometry.PointAt(_shape, _pointCount, lapFraction);
        }

        /// <summary>
        /// Direction of travel at <paramref name="lapFraction"/>, as an angle in degrees
        /// for a sprite that points UP (+Y) - i.e. ready for Quaternion.Euler(0, 0, angle).
        /// Taken across a few points either side, so the marker follows the road rather
        /// than twitching at every decimated vertex. Allocation-free.
        /// </summary>
        public float HeadingDegreesAt(float lapFraction)
        {
            if (!Ready) return 0f;
            Vector2 d = TangentAt(lapFraction);
            if (d.sqrMagnitude < 1e-10f) return 0f;
            return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg - 90f;
        }

        private Vector2 TangentAt(float lapFraction)
        {
            return MinimapGeometry.TangentAt(_shape, _pointCount, lapFraction);
        }

        /// <summary>
        /// A unit vector pointing AWAY from the track at <paramref name="lapFraction"/>,
        /// in this rect's local space - where a turn badge goes so that it annotates the
        /// corner instead of covering it. Perpendicular to the local tangent, with the
        /// sign chosen to point away from the circuit's centroid.
        /// </summary>
        public Vector2 OutwardAt(float lapFraction)
        {
            if (!Ready) return Vector2.up;
            return MinimapGeometry.OutwardAt(_shape, _pointCount, _centroid, lapFraction);
        }

        /// <summary>
        /// True when the integrated lap returns close enough to its own start for a
        /// closing span between the last point and the first to be honest. Threshold is a
        /// fraction of the map's own extent, so it holds at any track size.
        ///
        /// The V1 placeholder circuit did NOT close - it ended on a heading of 259 degrees
        /// with its final point most of the map away from its first - and drawing the
        /// closing chord anyway painted a long straight the player never drives, which
        /// reads as "the race ended early" when the marker stops at the end of the real
        /// route (plan 11.5). The final circuit closes by construction, so this is now a
        /// regression guard rather than a workaround: if it ever reports false again, the
        /// track stopped closing.
        /// </summary>
        public bool ClosesOnItself
        {
            get { return Ready && _closureError < 0.08f; }
        }

        /// <summary>How far the lap misses its own start, as a fraction of the map's extent.</summary>
        public float ClosureError { get { return _closureError; } }

        /// <summary>The start/finish point, for the checkered marker.</summary>
        public Vector2 StartPosition { get { return Ready ? Scaled(_shape[0]) : Vector2.zero; } }

        /// <summary>
        /// Rotation for the start/finish marker, so the checkered bar lies ACROSS the
        /// road rather than along it. The marker art is a vertical bar, so it wants the
        /// travel direction turned through 90 degrees.
        /// </summary>
        public float StartMarkerDegrees { get { return HeadingDegreesAt(0f) + 90f; } }

        /// <summary>
        /// The one place normalised shape units become UI units.
        ///
        /// It fits the shape OWN box into the rect rather than fitting a square into the
        /// smaller side, and that is the difference between a map that uses the panel and
        /// one that sits in the middle of it. The old form took min(width, height) for
        /// both axes, so on a panel wider than it is tall every unused pixel of width was
        /// thrown away - measured at 28% of the width and 58% of the height in use.
        ///
        /// The scale is UNIFORM by construction: one number, used for both axes. Fitting
        /// each axis separately would fill the panel completely and draw a circuit the
        /// player is not driving.
        /// </summary>
        private Vector2 Scaled(Vector2 normalised)
        {
            Rect r = rectTransform.rect;
            float availX = Mathf.Max(1f, r.width - padding * 2f);
            float availY = Mathf.Max(1f, r.height - padding * 2f);
            float s = Mathf.Min(availX / Mathf.Max(0.0001f, _extent.x),
                                availY / Mathf.Max(0.0001f, _extent.y));
            return normalised * s;
        }

        /// <summary>
        /// Half the box the drawn map occupies, in this rect own units - what a badge has
        /// to be clamped inside. Per axis, because the panel is no longer square.
        /// </summary>
        public Vector2 DrawnHalfExtent
        {
            get
            {
                return new Vector2(Mathf.Abs(Scaled(new Vector2(_extent.x, 0f)).x) * 0.5f,
                                   Mathf.Abs(Scaled(new Vector2(0f, _extent.y)).y) * 0.5f);
            }
        }

        /// <summary>The normalised bounding box of the shape - the longer side is 1.</summary>
        public Vector2 Extent { get { return _extent; } }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!Ready) return;

            int spans = ClosesOnItself ? _pointCount : _pointCount - 1;
            int dash = Mathf.Max(1, dashSpans);

            // TWO PASSES, ONE MESH. uGUI has no depth test, so triangles simply land in
            // the order they are added: every dark body quad first, then every coloured
            // dash over the top. Interleaving them would let one span's casing paint over
            // the previous span's dash at the joint.
            Color32 body = MinimapArt.BodyColor * color;
            for (int i = 0; i < spans; i++)
                AddSpan(vh, i, bodyWidth * 0.5f, body);

            for (int i = 0; i < spans; i++)
            {
                if ((i / dash) % 2 != 0) continue;
                Color32 c = MinimapArt.SectorColor(_sectorAt[i]) * color;
                AddSpan(vh, i, lineWidth * 0.5f, c);
            }
        }

        /// <summary>
        /// One quad along span <paramref name="i"/>. Joints are left square: at this size
        /// a mitre is invisible and would double the vertex count for nothing.
        /// </summary>
        private void AddSpan(VertexHelper vh, int i, float half, Color32 c)
        {
            Vector2 a = Scaled(_shape[i]);
            Vector2 b = Scaled(_shape[(i + 1) % _pointCount]);

            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.0001f) return;
            Vector2 nrm = new Vector2(-d.y, d.x) / len * half;

            int v = vh.currentVertCount;
            vh.AddVert(a - nrm, c, Vector2.zero);
            vh.AddVert(a + nrm, c, Vector2.zero);
            vh.AddVert(b + nrm, c, Vector2.zero);
            vh.AddVert(b - nrm, c, Vector2.zero);
            vh.AddTriangle(v, v + 1, v + 2);
            vh.AddTriangle(v, v + 2, v + 3);

            // ANTI-ALIASED EDGES. The build renders this at the canvas's real pixel size
            // with no MSAA (the only quality level, "Web", has antiAliasing 0), so a
            // 3-unit diagonal line is a hard staircase - while the Editor's Game view is
            // often shown scaled, which filters it. A fringe one
            // SCREEN pixel wide on each side, fading to transparent, gives the same soft
            // edge at any device pixel ratio for 8 extra vertices per span, in the same
            // mesh and the same draw call.
            Vector2 f = nrm / half * EdgeFeather();
            Color32 clear = c; clear.a = 0;
            AddFringe(vh, a + nrm, b + nrm, a + nrm + f, b + nrm + f, c, clear);
            AddFringe(vh, a - nrm, b - nrm, a - nrm - f, b - nrm - f, c, clear);
        }

        private static void AddFringe(VertexHelper vh, Vector2 a, Vector2 b, Vector2 aOut, Vector2 bOut,
                                      Color32 inner, Color32 outer)
        {
            int v = vh.currentVertCount;
            vh.AddVert(a, inner, Vector2.zero);
            vh.AddVert(aOut, outer, Vector2.zero);
            vh.AddVert(bOut, outer, Vector2.zero);
            vh.AddVert(b, inner, Vector2.zero);
            vh.AddTriangle(v, v + 1, v + 2);
            vh.AddTriangle(v, v + 2, v + 3);
        }

        /// <summary>One screen pixel, in this graphic's own units.</summary>
        private float EdgeFeather()
        {
            Canvas c = canvas;
            float scale = c != null ? c.scaleFactor : 1f;
            return 1f / Mathf.Max(0.05f, scale);
        }
    }

    /// <summary>
    /// The shape maths, lifted clear of MonoBehaviour so it can be checked by
    /// <c>SectorProbe</c> without a GameObject, a Canvas or Play Mode. Everything here is
    /// a pure function of the track.
    /// </summary>
    public static class MinimapGeometry
    {
        /// <summary>
        /// Walk EVERY segment, integrating heading, but keep only <paramref name="n"/> of
        /// the positions. Sampling the heading instead of integrating it would cut corners
        /// - literally.
        ///
        /// `curve` is the second difference of the road's lateral offset, so heading
        /// changes by curve / SegmentLength per segment - the same relationship
        /// RoadGeometry.RoadYawPerCurveUnit expresses per unit of z.
        ///
        /// Points are chosen by bucket (<c>i * n / segments</c>) rather than by a fixed
        /// integer stride. An integer stride does not divide the segment count, so it
        /// leaves a ragged tail of up to a full stride undrawn at the end of the lap -
        /// which is a gap at the start/finish line, the one place on the map a gap is
        /// read as "the track is broken here".
        ///
        /// Point 0 is the start/finish line itself, at heading 0 and the origin.
        /// <paramref name="endpoint"/> receives the position after the LAST segment, which
        /// is where the lap actually closes.
        /// </summary>
        public static int Integrate(TrackModel track, int n, Vector2[] shape, float[] cumulative,
                                    int[] segmentAt, out Vector2 endpoint)
        {
            endpoint = Vector2.zero;
            if (track == null || shape == null || n < 2) return 0;

            int segs = track.Count;
            float heading = 0f;
            float x = 0f, y = 0f;
            int kept = 0;

            for (int i = 0; i < segs; i++)
            {
                int bucket = (int)(((long)i * n) / segs);
                if (bucket >= kept && kept < n)
                {
                    shape[kept] = new Vector2(x, y);
                    if (cumulative != null) cumulative[kept] = (float)i / segs;
                    if (segmentAt != null) segmentAt[kept] = i;
                    kept++;
                }

                heading += track[i].curve / RoadGeometry.SegmentLength;
                x += Mathf.Sin(heading);
                y += Mathf.Cos(heading);
            }

            endpoint = new Vector2(x, y);
            return kept;
        }

        /// <summary>
        /// Fit the integrated shape into a unit box IN PLACE, preserving aspect, and
        /// report the centre that was removed and the scale that was applied.
        ///
        /// Preserving aspect matters: a lap squashed to fill a square rect stops looking
        /// like the track the player is driving, which defeats the point.
        /// </summary>
        /// <summary>
        /// Quarter turn COUNTER-CLOCKWISE, applied to the integrated shape before it is
        /// fitted - which is how one transform reaches everything.
        ///
        /// The circuit is integrated from the start/finish heading NORTH, so it comes out
        /// tall and narrow with the pit straight running up the side. The reference the
        /// client traced has it the other way round: long axis horizontal, pit straight
        /// along the bottom, T01 at the bottom left. This is that quarter turn, and doing
        /// it HERE rather than as a rotation on the RectTransform matters twice over - a
        /// rotated RectTransform would take the frame and the badge rects round with it,
        /// and everything downstream is derived from this array and so inherits the turn
        /// for free: the player marker heading, the outward normals the badges sit on, the
        /// sector dashes, the angle of the start/finish bar.
        ///
        /// It is presentation and nothing else. TrackModel is untouched and the car drives
        /// the same road it always did.
        /// </summary>
        public static Vector2 RotateCcw(Vector2 v) { return new Vector2(-v.y, v.x); }

        public static void Normalise(Vector2[] shape, int count, out Vector2 centre, out float scale)
        {
            Vector2 unused;
            Normalise(shape, count, out centre, out scale, out unused);
        }

        /// <summary>
        /// As above, and additionally reports the shape bounding box AFTER normalising -
        /// the longer side is 1 and the shorter is less, which is what lets a caller fit a
        /// long circuit into a wide panel at a uniform scale instead of into the largest
        /// square that fits inside it.
        /// </summary>
        public static void Normalise(Vector2[] shape, int count, out Vector2 centre,
                                     out float scale, out Vector2 extent)
        {
            centre = Vector2.zero;
            scale = 1f;
            extent = Vector2.one;
            if (shape == null || count < 2) return;

            for (int i = 0; i < count; i++) shape[i] = RotateCcw(shape[i]);

            float minX = shape[0].x, maxX = minX, minY = shape[0].y, maxY = minY;
            for (int i = 1; i < count; i++)
            {
                if (shape[i].x < minX) minX = shape[i].x; else if (shape[i].x > maxX) maxX = shape[i].x;
                if (shape[i].y < minY) minY = shape[i].y; else if (shape[i].y > maxY) maxY = shape[i].y;
            }

            float w = Mathf.Max(0.0001f, maxX - minX);
            float h = Mathf.Max(0.0001f, maxY - minY);
            scale = 1f / Mathf.Max(w, h);
            extent = new Vector2(w * scale, h * scale);
            Vector2 box = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);

            Vector2 mean = Vector2.zero;
            for (int i = 0; i < count; i++)
            {
                shape[i] = (shape[i] - box) * scale;
                mean += shape[i];
            }
            centre = mean / count;
        }

        /// <summary>
        /// Position at a lap fraction, in the shape's own (normalised) space. Wrapped, not
        /// clamped: the circuit closes, so fraction 0.99 interpolates toward the start.
        /// </summary>
        public static Vector2 PointAt(Vector2[] shape, int count, float fraction)
        {
            if (shape == null || count < 2) return Vector2.zero;
            float f = Mathf.Repeat(fraction, 1f) * count;
            int i = Mathf.Clamp((int)f, 0, count - 1);
            return Vector2.Lerp(shape[i], shape[(i + 1) % count], f - i);
        }

        /// <summary>
        /// Direction of travel, taken across a few points either side so a marker follows
        /// the road rather than twitching at every decimated vertex. Wrapped for the same
        /// reason as <see cref="PointAt"/> - clamping would make the player arrow snap as
        /// the lap rolls over the start/finish line.
        /// </summary>
        public static Vector2 TangentAt(Vector2[] shape, int count, float fraction)
        {
            if (shape == null || count < 2) return Vector2.up;
            float f = Mathf.Repeat(fraction, 1f) * count;
            int i = Mathf.Clamp((int)f, 0, count - 1);
            int a = ((i - 2) % count + count) % count;
            int b = (i + 3) % count;
            return shape[b] - shape[a];
        }

        /// <summary>
        /// Unit vector perpendicular to the track and pointing away from the circuit's
        /// centroid - where an annotation goes so that it does not cover the thing it
        /// annotates. Falls back to the radial direction on a degenerate tangent.
        /// </summary>
        public static Vector2 OutwardAt(Vector2[] shape, int count, Vector2 centroid, float fraction)
        {
            Vector2 t = TangentAt(shape, count, fraction);
            Vector2 radial = PointAt(shape, count, fraction) - centroid;
            Vector2 nrm = new Vector2(-t.y, t.x);

            if (nrm.sqrMagnitude < 1e-10f)
                nrm = radial.sqrMagnitude > 1e-10f ? radial : Vector2.up;
            else if (Vector2.Dot(nrm, radial) < 0f)
                nrm = -nrm;

            return nrm.sqrMagnitude > 1e-10f ? nrm.normalized : Vector2.up;
        }
    }

    /// <summary>
    /// Where the fifteen turn badges go.
    ///
    /// A 160x160 minimap is a small place to put fifteen numbered corners. Two apexes can
    /// be close together along the road (T09/T10 are 50 segments apart) and two more can
    /// be close on the MAP without being close on the track at all, because the circuit
    /// folds over itself - on this layout T01 and T07 land within 24 units of each other
    /// although half a lap separates them. Dropping a badge on each apex overlaps several
    /// of them; hiding the second of a pair loses a corner number, which is the one thing
    /// a numbered badge exists to provide.
    ///
    /// So every badge starts <paramref name="offset"/> outside the track at its own apex
    /// and is then RELAXED: colliding pairs push each other apart, and after each pass
    /// every badge is pulled back to within <paramref name="maxDisplacement"/> of the
    /// corner it names and inside the map. A few passes settle it.
    ///
    /// The first thing tried was simpler - push a colliding badge one ring further out,
    /// then two - and it is worth saying why it is not here. Greedy outward pushing solved
    /// the pair it was looking at and created a new collision with a third badge, and it
    /// sent one badge 43 units from its own corner on a map only 140 wide. A badge that far
    /// from its apex is not annotating anything. Symmetric relaxation moves both members of
    /// a pair by half as much, sees the third badge on the next pass, and on this circuit
    /// clears every overlap while moving no badge more than about 12 units.
    ///
    /// Separated from the MonoBehaviour so SectorProbe can MEASURE the resulting
    /// separation instead of anyone eyeballing a screenshot.
    /// </summary>
    public static class MinimapBadges
    {
        /// <summary>
        /// Relaxation passes. Deterministic, and it runs once when the HUD is built - this
        /// is 15 badges, so the whole thing is a few hundred distance tests.
        /// </summary>
        public const int Passes = 16;

        /// <summary>
        /// Place badges for apexes given their positions ON the line and their outward
        /// normals. Returns the worst centre-to-centre separation produced, so a caller
        /// can report whether anything still overlaps.
        /// </summary>
        public static float Place(Vector2[] onTrack, Vector2[] outward, int count,
                                  float offset, float minSeparation, float maxDisplacement,
                                  float clampHalf, Vector2[] result)
        {
            return Place(onTrack, outward, count, offset, minSeparation, maxDisplacement,
                         new Vector2(clampHalf, clampHalf), result);
        }

        /// <summary>
        /// As above with a per-AXIS clamp. The panel is no longer square, so one number
        /// for both axes either lets a badge hang off the short side or squeezes the long
        /// one for no reason.
        /// </summary>
        public static float Place(Vector2[] onTrack, Vector2[] outward, int count,
                                  float offset, float minSeparation, float maxDisplacement,
                                  Vector2 clampHalf, Vector2[] result)
        {
            if (onTrack == null || outward == null || result == null || count <= 0) return 0f;

            for (int i = 0; i < count; i++)
                result[i] = onTrack[i] + outward[i] * offset;

            for (int pass = 0; pass < Passes; pass++)
            {
                bool moved = false;

                for (int i = 0; i < count; i++)
                    for (int j = i + 1; j < count; j++)
                    {
                        Vector2 d = result[j] - result[i];
                        float len = d.magnitude;
                        if (len >= minSeparation) continue;

                        moved = true;
                        // Exactly coincident badges have no direction to separate along;
                        // borrow one's outward normal rather than dividing by zero.
                        if (len < 1e-4f) { d = outward[i]; len = 1f; }

                        Vector2 push = d * ((minSeparation - len) * 0.5f / len);
                        result[i] -= push;
                        result[j] += push;
                    }

                for (int i = 0; i < count; i++)
                {
                    Vector2 v = result[i] - onTrack[i];
                    float len = v.magnitude;
                    if (len > maxDisplacement && len > 1e-4f)
                        result[i] = onTrack[i] + v * (maxDisplacement / len);

                    // Never outside the map's own rect: a badge hanging past the frame
                    // reads as a rendering fault, and on a phone it would sit over the road.
                    result[i].x = Mathf.Clamp(result[i].x, -clampHalf.x, clampHalf.x);
                    result[i].y = Mathf.Clamp(result[i].y, -clampHalf.y, clampHalf.y);
                }

                if (!moved) break;
            }

            if (count < 2) return 0f;
            float worst = float.MaxValue;
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                {
                    float d = (result[i] - result[j]).magnitude;
                    if (d < worst) worst = d;
                }
            return worst;
        }
    }

    /// <summary>
    /// The minimap's own tiny atlas: the fifteen turn-number badges and the checkered
    /// start/finish bar, cut from the Phase 11 sheet by Tools/ArtPrep/prepare_minimap.py,
    /// plus the sector and body colours SAMPLED from that sheet's dashed strips.
    ///
    /// Separate from <see cref="UiArt"/>'s ui_atlas on purpose: 7 KB of PNG that only the
    /// minimap needs, loaded through Resources exactly as ui_atlas is, so nothing in
    /// Main.unity has to change. Same degradation contract too - a missing atlas means
    /// <see cref="Badge"/> returns null and the colours fall back to the constants below,
    /// so the map is plainer rather than broken.
    /// </summary>
    public static class MinimapArt
    {
        public const string AtlasPath = "UI/minimap_atlas";
        public const int MaxSectors = 8;

        /// <summary>
        /// Fallbacks, and the sheet's measured values: body #1F1F1F, sector 1 #02A6F9
        /// blue, sector 2 #F9D504 yellow, sector 3 #EE010C red. They are duplicated here
        /// so a build without the atlas still draws the map in the right colours.
        /// </summary>
        private static readonly Color32 FallbackBody = new Color32(31, 31, 31, 255);

        private static readonly Color32[] FallbackSectors =
        {
            new Color32(2, 166, 249, 255),
            new Color32(249, 213, 4, 255),
            new Color32(238, 1, 12, 255),
            new Color32(255, 255, 255, 255),
        };

        [Serializable]
        private sealed class Entry
        {
            public string name;
            public float[] rect;
            public float[] pivot;
            public float[] border;
            public float scale;
            public float[] size;
        }

        [Serializable]
        private sealed class AtlasFile
        {
            public int width;
            public int height;
            public Entry[] sprites;
            public int[] bodyColor;       // r, g, b
            public int[] sectorColors;    // r, g, b per sector, FLAT - JsonUtility cannot
                                          // deserialise an array of arrays.
        }

        private static bool _attempted;
        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, Vector2> Sizes = new Dictionary<string, Vector2>();
        private static Color32 _body = FallbackBody;
        private static Color32[] _sectors = FallbackSectors;

        /// <summary>True once the atlas has been found and parsed.</summary>
        public static bool Available { get { EnsureLoaded(); return Sprites.Count > 0; } }

        public static Color32 BodyColor { get { EnsureLoaded(); return _body; } }

        /// <summary>
        /// Sector <paramref name="sector"/>'s colour, 0-based. Wraps rather than throws:
        /// a circuit with more sectors than the sheet has colours should recycle them, not
        /// take the HUD down.
        /// </summary>
        public static Color32 SectorColor(int sector)
        {
            EnsureLoaded();
            if (_sectors == null || _sectors.Length == 0) return FallbackSectors[0];
            if (sector < 0) sector = 0;
            return _sectors[sector % _sectors.Length];
        }

        /// <summary>The badge for turn <paramref name="number"/> (1..15), or null.</summary>
        public static Sprite Badge(int number)
        {
            EnsureLoaded();
            Sprite s;
            return Sprites.TryGetValue(BadgeName(number), out s) ? s : null;
        }

        public static Vector2 BadgeSize(int number, Vector2 fallback)
        {
            EnsureLoaded();
            Vector2 v;
            return Sizes.TryGetValue(BadgeName(number), out v) ? v : fallback;
        }

        /// <summary>Sprite by name - "minimap_start" is the checkered bar.</summary>
        public static Sprite Get(string name)
        {
            EnsureLoaded();
            Sprite s;
            return Sprites.TryGetValue(name, out s) ? s : null;
        }

        public static Vector2 Size(string name, Vector2 fallback)
        {
            EnsureLoaded();
            Vector2 v;
            return Sizes.TryGetValue(name, out v) ? v : fallback;
        }

        /// <summary>
        /// Badge names are fixed-width and assembled by hand rather than with
        /// <c>number.ToString("00")</c>: the format string follows the running culture's
        /// digits, and the browser's culture is the player's, not ours (the same reasoning
        /// as RaceSession.FormatTime). A mismatched key would silently return no badge.
        /// </summary>
        private static string BadgeName(int number)
        {
            if (number < 0) number = 0;
            int tens = (number / 10) % 10;
            int ones = number % 10;
            return "minimap_turn_" + (char)('0' + tens) + (char)('0' + ones);
        }

        private static void EnsureLoaded()
        {
            if (_attempted) return;
            _attempted = true;

            Texture2D tex = Resources.Load<Texture2D>(AtlasPath);
            TextAsset json = Resources.Load<TextAsset>(AtlasPath);
            if (tex == null || json == null)
            {
                Debug.LogWarning("[MinimapArt] Resources/" + AtlasPath
                                 + " not found - the minimap will draw without turn badges.");
                return;
            }

            AtlasFile file;
            try { file = JsonUtility.FromJson<AtlasFile>(json.text); }
            catch (Exception e)
            {
                Debug.LogError("[MinimapArt] could not parse " + AtlasPath + ".json: " + e.Message);
                return;
            }
            if (file == null) return;

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            if (file.bodyColor != null && file.bodyColor.Length >= 3)
                _body = new Color32((byte)file.bodyColor[0], (byte)file.bodyColor[1],
                                    (byte)file.bodyColor[2], 255);

            if (file.sectorColors != null && file.sectorColors.Length >= 3)
            {
                int n = file.sectorColors.Length / 3;
                Color32[] cols = new Color32[n];
                for (int i = 0; i < n; i++)
                    cols[i] = new Color32((byte)file.sectorColors[3 * i],
                                          (byte)file.sectorColors[3 * i + 1],
                                          (byte)file.sectorColors[3 * i + 2], 255);
                _sectors = cols;
            }

            if (file.sprites == null) return;
            for (int i = 0; i < file.sprites.Length; i++)
            {
                Entry e = file.sprites[i];
                if (e == null || string.IsNullOrEmpty(e.name) || e.rect == null || e.rect.Length < 4)
                    continue;

                float scale = e.scale > 0f ? e.scale : 1f;
                Vector2 pivot = (e.pivot != null && e.pivot.Length >= 2)
                    ? new Vector2(e.pivot[0], e.pivot[1]) : new Vector2(0.5f, 0.5f);

                // pixelsPerUnit = scale * 100, exactly as UiArt does it, so a sprite drawn
                // at its design size under a referencePixelsPerUnit-100 CanvasScaler is
                // the size prepare_minimap.py said it would be.
                Sprite s = Sprite.Create(tex, new Rect(e.rect[0], e.rect[1], e.rect[2], e.rect[3]),
                                         pivot, 100f * scale, 0, SpriteMeshType.FullRect);
                s.name = e.name;
                Sprites[e.name] = s;

                Sizes[e.name] = (e.size != null && e.size.Length >= 2)
                    ? new Vector2(e.size[0], e.size[1])
                    : new Vector2(e.rect[2] / scale, e.rect[3] / scale);
            }
        }
    }
}
