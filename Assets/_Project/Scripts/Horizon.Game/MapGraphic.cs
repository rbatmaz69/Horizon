using Horizon.World;
using UnityEngine;
using UnityEngine.UI;

namespace Horizon.Game
{
    /// <summary>
    /// Draws a <see cref="WorldMap"/> into a uGUI rect: roads as stroked lines, water and towns as
    /// filled shapes, features as small marks. One component, used by both the minimap in the corner
    /// and the full-screen map behind it.
    ///
    /// <para><b>A mesh rather than a texture, and a mesh rather than a camera.</b> A baked image of a
    /// world nineteen kilometres across would be twelve metres to the pixel at 2048, and a road is seven
    /// metres wide — the map would be a blur exactly where it carries its information. A camera cannot
    /// do it either, for the two reasons <see cref="WorldMap"/> gives. Lines drawn from the data are
    /// crisp at every zoom, cost one draw call, and need no texture at all: uGUI's default material
    /// multiplies the vertex colour by a white texture, so colour is all these vertices carry.</para>
    ///
    /// <para><b>No allocation per rebuild.</b> <c>VertexHelper</c> is uGUI's own pooled instance and
    /// reuses its lists, and everything else here — the visited stamps, the four segment buckets — is
    /// sized once against the map and then reused. The minimap rebuilds most frames the car is moving,
    /// so this is the same rule the rest of the HUD follows: cache, gate, never build.</para>
    ///
    /// <para><b>The <c>RequireComponent</c> above is not boilerplate.</b> <c>Graphic</c> declares one
    /// too, and it did not carry down to this class: built by <c>AddComponent&lt;MapGraphic&gt;()</c>
    /// without it, the object came up with no <c>CanvasRenderer</c> at all. <c>Graphic.Rebuild</c> opens
    /// with <c>if (canvasRenderer == null || canvasRenderer.cull) return;</c>, so every rebuild returned
    /// on its first line — no error, no warning, and a map that drew nothing while its labels, which are
    /// separate objects, sat in exactly the right places. The build was clean; the picture was empty.
    /// </para>
    ///
    /// <para><b>The street cull is structural, not cosmetic.</b> One canvas mesh holds 65 535 vertices,
    /// which is 16 383 quads. The roads of the drive itself are about three thousand segments and fit at
    /// any zoom; the four towns are another few thousand between them, and at a zoom where a town is
    /// forty units wide they are not streets any more but hatching. Dropping them past a threshold is
    /// what keeps the mesh legal and the towns legible at the same time.</para>
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MapGraphic : MaskableGraphic
    {
        [Tooltip("Baked by Rebuild Prototype Scene into Art/Models/Generated/WorldMap.asset.")]
        [SerializeField] private WorldMap map;

        [Header("Palette")]
        [SerializeField] private Color waterColour = new Color(0.16f, 0.31f, 0.42f, 1f);
        [SerializeField] private Color townColour = new Color(0.20f, 0.20f, 0.22f, 1f);
        [SerializeField] private Color streetColour = new Color(0.42f, 0.42f, 0.44f, 1f);
        [SerializeField] private Color trunkColour = new Color(0.78f, 0.78f, 0.80f, 1f);
        [SerializeField] private Color motorwayColour = new Color(0.96f, 0.55f, 0.28f, 1f);

        [Header("Marks")]
        [SerializeField] private Color placeColour = new Color(1f, 1f, 1f, 0.95f);
        [SerializeField] private Color fuelColour = new Color(0.96f, 0.55f, 0.28f, 0.98f);
        [SerializeField] private Color viewpointColour = new Color(0.52f, 0.79f, 0.62f, 0.95f);
        [SerializeField] private Color structureColour = new Color(0.62f, 0.66f, 0.74f, 0.92f);

        [Tooltip("Half-size of a marker, in canvas units. Fixed rather than scaled with the zoom: a "
               + "symbol that shrinks with the world stops being a symbol.")]
        [SerializeField] private float markerRadius = 7f;

        [SerializeField] private bool showMarkers = true;

        [Tooltip("Clip everything drawn to the largest circle that fits this rect. The minimap is round; "
               + "the full-screen map is not.")]
        [SerializeField] private bool circular;

        [Tooltip("Fraction of the rect's half-width the circular clip uses. The minimap's rim is a ring "
               + "with a hole at 0.8 of its radius, and the map has to end where that hole ends.")]
        [SerializeField] private float clipFraction = 1f;

        [Tooltip("Past this many metres to the unit, only the start places keep a mark. Forty tunnels, "
               + "viewpoints and pumps on a view of the whole world are not marks but a rash.")]
        [SerializeField] private float markerZoomLimit = 9f;

        [Tooltip("Past this many metres to the unit, town streets are dropped. See the class note — "
               + "this is what keeps one canvas mesh inside its vertex limit.")]
        [SerializeField] private float streetZoomLimit = 9f;

        [Header("Minimum stroke widths, canvas units")]
        [SerializeField] private float minRiverWidth = 2.5f;
        [SerializeField] private float minStreetWidth = 1.6f;
        [SerializeField] private float minTrunkWidth = 3f;
        [SerializeField] private float minMotorwayWidth = 3.5f;

        /// <summary>
        /// Where the emission stops.
        ///
        /// <para>Short of the 65 535 a canvas mesh holds, so the last shape started always has room to
        /// finish. Running past the limit does not draw a partial map — uGUI drops the whole mesh.</para>
        /// </summary>
        private const int VertexBudget = 60000;

        /// <summary>Longest mitre, in half-widths. See <see cref="Mitre"/>.</summary>
        private const float MaxMitre = 2f;

        /// <summary>
        /// Sides of the polygon the circular clip actually uses.
        ///
        /// <para>Thirty-two, which on a 150-unit radius sits 0.7 units inside the true circle. That is
        /// under half the width of the thinnest line drawn, and the rim is thirty units thick.</para>
        /// </summary>
        private const int ClipEdges = 32;

        private Vector2 centre;
        private float metresPerUnit = 4f;
        private float headingDegrees;

        private float cos = 1f;
        private float sin;
        private Vector2 rectCentre;

        // Which segments this rebuild has already taken. A segment is enrolled in every grid cell its
        // bounds touch, so without this a road along a cell boundary is drawn twice.
        private int[] stamp;
        private int stampId;

        // Segment indices for this rebuild, split by line kind so they can be emitted in draw order
        // from a single walk of the grid.
        private int[][] byKind;
        private int[] byKindCount;

        // Clipping scratch. Two buffers, ping-ponged, sized once: a town hull of forty points against
        // thirty-two half-planes cannot exceed this, and nothing here may allocate per rebuild.
        private readonly Vector2[] clipFront = new Vector2[192];
        private readonly Vector2[] clipBack = new Vector2[192];

        private float clipRadius;
        private float clipInradius;

        public WorldMap Map => map;

        /// <summary>
        /// What the last rebuild put in the mesh.
        ///
        /// <para>Reported by the preview tool. A map that draws nothing looks exactly like a map whose
        /// data is empty and exactly like a canvas that never rebuilt, and the difference between those
        /// three is the difference between a morning and a minute.</para>
        /// </summary>
        /// <summary>−1 until the first rebuild, which is how "drew nothing" is told from "never ran".</summary>
        public int LastVertexCount { get; private set; } = -1;

        public int LastSegmentCount { get; private set; }

        public int LastAreaCount { get; private set; }

        /// <summary>The tint a town's outline is filled with, for the key.</summary>
        public Color TownColour => townColour;

        public Vector2 Centre => centre;

        public float MetresPerUnit => metresPerUnit;

        public float HeadingDegrees => headingDegrees;

        /// <summary>
        /// Aims the view. Rebuilds only when something moved, so a stopped car costs nothing.
        /// </summary>
        public void SetView(Vector2 worldCentre, float metres, float heading)
        {
            if (centre == worldCentre
                && Mathf.Approximately(metresPerUnit, metres)
                && Mathf.Approximately(headingDegrees, heading))
            {
                return;
            }

            centre = worldCentre;
            metresPerUnit = Mathf.Max(0.05f, metres);
            headingDegrees = heading;

            SetVerticesDirty();
        }

        public void SetMap(WorldMap value)
        {
            map = value;
            SetVerticesDirty();
        }

        /// <summary>
        /// Where a world point lands in this rect.
        ///
        /// <para>Public because the labels and the car marker are real uGUI objects rather than
        /// triangles in this mesh — text has to be text. Children anchored at the centre of this rect
        /// can use the result as their <c>anchoredPosition</c> directly.</para>
        /// </summary>
        public Vector2 LocalPointOf(Vector2 world)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);

            float dx = world.x - centre.x;
            float dz = world.y - centre.y;

            return new Vector2(c * dx - s * dz, s * dx + c * dz) / metresPerUnit
                   + rectTransform.rect.center;
        }

        /// <summary>Metres per unit that fits the whole world into this rect, with a margin.</summary>
        public float FitScale()
        {
            if (map == null)
            {
                return metresPerUnit;
            }

            Rect area = rectTransform.rect;
            Vector2 size = map.PlanSize;

            if (area.width < 1f || area.height < 1f)
            {
                return metresPerUnit;
            }

            return Mathf.Max(size.x / area.width, size.y / area.height) * 1.04f;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            LastVertexCount = 0;
            LastSegmentCount = 0;
            LastAreaCount = 0;

            if (map == null || map.PointCount < 2)
            {
                return;
            }

            Rect area = rectTransform.rect;
            rectCentre = area.center;

            // The clip is the largest circle the rect holds. Zero means "no clip", which is what the
            // full-screen map wants.
            // Not the full half-width: the map ends at the rim's inner edge. Clipped to the outer edge
            // instead, the last thirty units of road run *under* a ring drawn at 30 % alpha and show
            // through it — which reads exactly like a clip that is not working, and cost a while to
            // tell apart from one. The roads were always inside the circle; the circle was the wrong one.
            clipRadius = circular
                ? Mathf.Min(area.width, area.height) * 0.5f * Mathf.Clamp01(clipFraction)
                : 0f;
            clipInradius = clipRadius * Mathf.Cos(Mathf.PI / ClipEdges);

            float radians = headingDegrees * Mathf.Deg2Rad;
            cos = Mathf.Cos(radians);
            sin = Mathf.Sin(radians);

            VisibleBounds(out Vector2 min, out Vector2 max);

            // Towns under the water, so a dredged basin still reads as water inside the town it serves.
            EmitAreas(vh, MapAreaKind.Town, townColour, min, max);
            EmitAreas(vh, MapAreaKind.Water, waterColour, min, max);

            Collect(min, max);

            EmitLines(vh, MapLineKind.River, minRiverWidth);
            EmitLines(vh, MapLineKind.Street, minStreetWidth);
            EmitLines(vh, MapLineKind.Trunk, minTrunkWidth);
            EmitLines(vh, MapLineKind.Motorway, minMotorwayWidth);

            if (showMarkers)
            {
                EmitMarkers(vh, min, max);
            }

            LastVertexCount = vh.currentVertCount;
        }

        /// <summary>
        /// The world-space bounds of this rect, which is the block of grid cells worth looking at.
        ///
        /// <para>From the four corners rather than from the rect's own size: the view turns with the car,
        /// and a rotated rect covers a larger axis-aligned box than the one it was cut from.</para>
        /// </summary>
        private void VisibleBounds(out Vector2 min, out Vector2 max)
        {
            Rect area = rectTransform.rect;

            min = new Vector2(float.MaxValue, float.MaxValue);
            max = new Vector2(float.MinValue, float.MinValue);

            for (int i = 0; i < 4; i++)
            {
                float lx = ((i & 1) == 0 ? area.xMin : area.xMax) - rectCentre.x;
                float ly = ((i & 2) == 0 ? area.yMin : area.yMax) - rectCentre.y;

                // The transform in LocalPointOf, run backwards: its rotation is orthonormal, so the
                // inverse is the transpose.
                var world = new Vector2(
                    (cos * lx + sin * ly) * metresPerUnit + centre.x,
                    (-sin * lx + cos * ly) * metresPerUnit + centre.y);

                min = Vector2.Min(min, world);
                max = Vector2.Max(max, world);
            }
        }

        private Vector2 ToLocal(Vector2 world)
        {
            float dx = world.x - centre.x;
            float dz = world.y - centre.y;

            return new Vector2(cos * dx - sin * dz, sin * dx + cos * dz) / metresPerUnit + rectCentre;
        }

        /// <summary>Walks the grid once and files every visible segment under its line's kind.</summary>
        private void Collect(Vector2 min, Vector2 max)
        {
            if (byKind == null)
            {
                byKind = new int[4][];
                byKindCount = new int[4];

                for (int i = 0; i < 4; i++)
                {
                    byKind[i] = new int[1024];
                }
            }

            for (int i = 0; i < 4; i++)
            {
                byKindCount[i] = 0;
            }

            if (stamp == null || stamp.Length < map.PointCount)
            {
                stamp = new int[map.PointCount];
                stampId = 0;
            }

            stampId++;

            bool streets = metresPerUnit <= streetZoomLimit;

            int fromColumn = Mathf.Max(0, map.ColumnOf(min.x));
            int toColumn = Mathf.Min(map.Columns - 1, map.ColumnOf(max.x));
            int fromRow = Mathf.Max(0, map.RowOf(min.y));
            int toRow = Mathf.Min(map.Rows - 1, map.RowOf(max.y));

            for (int row = fromRow; row <= toRow; row++)
            {
                for (int column = fromColumn; column <= toColumn; column++)
                {
                    map.CellRange(column, row, out int from, out int to);

                    for (int i = from; i < to; i++)
                    {
                        int point = map.ItemAt(i);

                        if (stamp[point] == stampId)
                        {
                            continue;
                        }

                        stamp[point] = stampId;

                        int kind = (int)map.KindOf(map.LineOfPoint(point));

                        if (!streets && kind == (int)MapLineKind.Street)
                        {
                            continue;
                        }

                        if (byKindCount[kind] == byKind[kind].Length)
                        {
                            var grown = new int[byKind[kind].Length * 2];
                            System.Array.Copy(byKind[kind], grown, byKind[kind].Length);
                            byKind[kind] = grown;
                        }

                        byKind[kind][byKindCount[kind]++] = point;
                        LastSegmentCount++;
                    }
                }
            }
        }

        private void EmitLines(VertexHelper vh, MapLineKind kind, float minWidth)
        {
            int index = (int)kind;
            int count = byKindCount[index];
            int[] segments = byKind[index];
            Color32 tint = ColourOf(kind);

            for (int i = 0; i < count; i++)
            {
                if (vh.currentVertCount >= VertexBudget)
                {
                    return;
                }

                int point = segments[i];
                int line = map.LineOfPoint(point);

                float half = Mathf.Max(minWidth * 0.5f, map.HalfWidthOf(line) / metresPerUnit);

                // Mitred at both ends against the neighbours this segment actually has, and butted where
                // it has none.
                float back = point > map.LineStartAt(line) ? Mitre(point - 1, point, point + 1, half) : 0f;
                float on = point + 2 < map.LineEndAt(line) ? Mitre(point, point + 1, point + 2, half) : 0f;

                AddQuad(vh, ToLocal(map.PointAt(point)), ToLocal(map.PointAt(point + 1)),
                    half, back, on, tint);
            }
        }

        private void EmitAreas(VertexHelper vh, MapAreaKind kind, Color colour, Vector2 min, Vector2 max)
        {
            Color32 tint = colour;

            for (int area = 0; area < map.AreaCount; area++)
            {
                if (map.AreaKindOf(area) != kind)
                {
                    continue;
                }

                int from = map.AreaStartAt(area);
                int to = map.AreaEndAt(area);

                if (to - from < 3 || vh.currentVertCount >= VertexBudget)
                {
                    continue;
                }

                if (!Overlaps(from, to, min, max))
                {
                    continue;
                }

                LastAreaCount++;

                // Fanned by AddConvex, which is only valid because both kinds of area are convex by
                // construction: a circle, and the hull of a town's junctions.
                int count = Mathf.Min(to - from, clipFront.Length);

                for (int p = 0; p < count; p++)
                {
                    clipFront[p] = ToLocal(map.AreaPointAt(from + p));
                }

                AddConvex(vh, count, tint);
            }
        }

        private bool Overlaps(int from, int to, Vector2 min, Vector2 max)
        {
            var ringMin = new Vector2(float.MaxValue, float.MaxValue);
            var ringMax = new Vector2(float.MinValue, float.MinValue);

            for (int p = from; p < to; p++)
            {
                Vector2 point = map.AreaPointAt(p);
                ringMin = Vector2.Min(ringMin, point);
                ringMax = Vector2.Max(ringMax, point);
            }

            return ringMax.x >= min.x && ringMin.x <= max.x && ringMax.y >= min.y && ringMin.y <= max.y;
        }

        private void EmitMarkers(VertexHelper vh, Vector2 min, Vector2 max)
        {
            bool features = metresPerUnit <= markerZoomLimit;

            for (int i = 0; i < map.MarkerCount; i++)
            {
                if (vh.currentVertCount >= VertexBudget)
                {
                    return;
                }

                if (!features && map.MarkerKindOf(i) != MapMarkerKind.Place)
                {
                    continue;
                }

                Vector2 at = map.MarkerAt(i);

                if (at.x < min.x || at.x > max.x || at.y < min.y || at.y > max.y)
                {
                    continue;
                }

                AddDiamond(vh, ToLocal(at), markerRadius, ColourOf(map.MarkerKindOf(i)));
            }
        }

        /// <summary>
        /// What a kind of line is drawn in.
        ///
        /// <para>Public because the full-screen map carries a key, and a key is a second drawing of the
        /// same palette. Read off the component that does the drawing, it cannot disagree with it; typed
        /// out again in <c>MenuUiSetup</c> it would agree until the first time somebody retuned one of
        /// them, and a key that quietly lies is worse than no key.</para>
        /// </summary>
        public Color ColourOf(MapLineKind kind)
        {
            switch (kind)
            {
                case MapLineKind.Motorway:
                    return motorwayColour;
                case MapLineKind.Trunk:
                    return trunkColour;
                case MapLineKind.Street:
                    return streetColour;
                default:
                    return waterColour;
            }
        }

        /// <summary>What a mark is drawn in. See <see cref="ColourOf(MapLineKind)"/>.</summary>
        public Color ColourOf(MapMarkerKind kind)
        {
            switch (kind)
            {
                case MapMarkerKind.FuelStation:
                    return fuelColour;
                case MapMarkerKind.Viewpoint:
                    return viewpointColour;
                case MapMarkerKind.Tunnel:
                case MapMarkerKind.Bridge:
                    return structureColour;
                default:
                    return placeColour;
            }
        }

        /// <summary>
        /// How far a segment must run past a joint for its outer corner to land on its neighbour's:
        /// the standard mitre, <c>half · tan(θ/2)</c>.
        ///
        /// <para><b>Not the half-width, which is what this did first.</b> Extending every segment by its
        /// own half-width closes the notch on the outside of a corner, but on a tight one it also throws
        /// the rectangle's corner well clear of the road — and the pass turns at 20 m with samples 12 m
        /// apart, so each joint swings some thirty-four degrees. The hairpin stack came back with a
        /// serrated outer edge, one tooth per sample, which is the one thing on the minimap the driver is
        /// actually reading. A mitre is exact, costs no extra vertices, and needs no trigonometry:
        /// <c>tan(θ/2)</c> is <c>|cross| / (1 + dot)</c> for unit vectors.</para>
        /// </summary>
        private float Mitre(int previous, int joint, int next, float half)
        {
            Vector2 a = map.PointAt(joint) - map.PointAt(previous);
            Vector2 b = map.PointAt(next) - map.PointAt(joint);

            float la = a.magnitude;
            float lb = b.magnitude;

            if (la < 0.0001f || lb < 0.0001f)
            {
                return 0f;
            }

            a /= la;
            b /= lb;

            float dot = a.x * b.x + a.y * b.y;

            // A hairpin taken in one sample would be a doubling back, and tan(θ/2) goes to infinity
            // there. The cap is what keeps a spike out of the picture.
            if (dot <= -0.999f)
            {
                return half * MaxMitre;
            }

            float cross = Mathf.Abs(a.x * b.y - a.y * b.x);
            return Mathf.Min(half * cross / (1f + dot), half * MaxMitre);
        }

        /// <summary>
        /// Emits one convex polygon, clipped to the round frame if there is one.
        ///
        /// <para><b>The clip is done here rather than by a uGUI <c>Mask</c>, and that was not the first
        /// choice.</b> A stencil mask is the standard way to make a round widget, and this widget was
        /// built with one — but it did not clip, in three successive frames taken through a target that
        /// was fixed twice on the way (no stencil format, then no sRGB) and still did not clip. What the
        /// mask was doing in a running game was unknowable from here, and shipping a shape that cannot
        /// be looked at is the one thing this project's tooling exists to prevent. Clipping the geometry
        /// is answerable in a picture, costs no extra pass on a tile GPU — which is the same argument
        /// <c>TouchUiSetup.ScrollList</c> makes against stencils — and makes the widget's shape a
        /// property of the thing that draws it rather than of a component standing over it.</para>
        ///
        /// <para>Nearly every polygon is wholly inside and takes the first branch. Only the few dozen
        /// straddling the rim are actually clipped, so the cost is a distance test per shape.</para>
        /// </summary>
        private void AddConvex(VertexHelper vh, int count, Color32 colour)
        {
            if (clipRadius > 0f)
            {
                count = ClipToFrame(count);

                if (count < 3)
                {
                    return;
                }
            }

            int first = vh.currentVertCount;

            for (int i = 0; i < count; i++)
            {
                vh.AddVert(clipFront[i], colour, Vector2.zero);
            }

            for (int k = 1; k + 1 < count; k++)
            {
                vh.AddTriangle(first, first + k, first + k + 1);
            }
        }

        /// <summary>
        /// Sutherland–Hodgman against the inscribed polygon, in place over the two scratch buffers.
        /// Returns the new vertex count, in <see cref="clipFront"/>.
        /// </summary>
        private int ClipToFrame(int count)
        {
            float nearest = float.MaxValue;
            float furthest = 0f;

            for (int i = 0; i < count; i++)
            {
                float distance = (clipFront[i] - rectCentre).magnitude;
                nearest = Mathf.Min(nearest, distance);
                furthest = Mathf.Max(furthest, distance);
            }

            // Wholly inside the inscribed polygon, which is the overwhelming majority.
            if (furthest <= clipInradius)
            {
                return count;
            }

            // Wholly outside the circle. Convex and every vertex beyond the rim, so nothing of it can
            // reach back inside.
            if (nearest >= clipRadius)
            {
                return 0;
            }

            for (int edge = 0; edge < ClipEdges; edge++)
            {
                float angle = edge * (Mathf.PI * 2f / ClipEdges);
                var normal = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

                int written = 0;

                for (int i = 0; i < count; i++)
                {
                    Vector2 current = clipFront[i];
                    Vector2 next = clipFront[(i + 1) % count];

                    float here = Vector2.Dot(current - rectCentre, normal) - clipInradius;
                    float there = Vector2.Dot(next - rectCentre, normal) - clipInradius;

                    if (here <= 0f)
                    {
                        clipBack[written++] = current;
                    }

                    if ((here > 0f) != (there > 0f))
                    {
                        clipBack[written++] = Vector2.Lerp(current, next, here / (here - there));
                    }

                    if (written >= clipBack.Length - 2)
                    {
                        break;
                    }
                }

                count = written;

                if (count < 3)
                {
                    return 0;
                }

                System.Array.Copy(clipBack, clipFront, count);
            }

            return count;
        }

        /// <summary>One segment, as a quad, mitred by <paramref name="back"/> and <paramref name="on"/>.</summary>
        private void AddQuad(
            VertexHelper vh, Vector2 a, Vector2 b, float half, float back, float on, Color32 colour)
        {
            Vector2 delta = b - a;
            float length = delta.magnitude;

            if (length < 0.0001f)
            {
                delta = new Vector2(1f, 0f);
                length = 1f;
            }

            Vector2 unit = delta / length;
            var across = new Vector2(-unit.y, unit.x) * half;

            Vector2 start = a - unit * back;
            Vector2 end = b + unit * on;

            clipFront[0] = start - across;
            clipFront[1] = start + across;
            clipFront[2] = end + across;
            clipFront[3] = end - across;

            AddConvex(vh, 4, colour);
        }

        private void AddDiamond(VertexHelper vh, Vector2 at, float radius, Color32 colour)
        {
            clipFront[0] = at + new Vector2(0f, radius);
            clipFront[1] = at + new Vector2(radius, 0f);
            clipFront[2] = at + new Vector2(0f, -radius);
            clipFront[3] = at + new Vector2(-radius, 0f);

            AddConvex(vh, 4, colour);
        }
    }
}
