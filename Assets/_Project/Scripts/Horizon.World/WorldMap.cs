using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// What a line on the map is, which decides how wide and what colour it is drawn.
    ///
    /// <para>The order is the draw order: <see cref="MapGraphic"/> emits every kind in turn and uGUI
    /// draws a canvas mesh in the order its triangles were added, so a motorway crosses over a river
    /// rather than under it.</para>
    /// </summary>
    public enum MapLineKind : byte
    {
        /// <summary>A river or a strait — a corridor of water with a spine and a half-width.</summary>
        River = 0,

        /// <summary>One street of one town.</summary>
        Street = 1,

        /// <summary>One of the six roads the drive itself is made of.</summary>
        Trunk = 2,

        /// <summary>One carriageway of the motorway, or the link off it.</summary>
        Motorway = 3,
    }

    /// <summary>A filled shape rather than a stroked line. Both kinds are convex, so both fan.</summary>
    public enum MapAreaKind : byte
    {
        /// <summary>A lake, a sea or a harbour basin: a circle about a centre.</summary>
        Water = 0,

        /// <summary>A town's footprint.</summary>
        Town = 1,
    }

    /// <summary>Something worth a symbol and a name.</summary>
    public enum MapMarkerKind : byte
    {
        /// <summary>One of the places the start screen offers.</summary>
        Place = 0,

        FuelStation = 1,
        Viewpoint = 2,
        Tunnel = 3,
        Bridge = 4,
    }

    /// <summary>
    /// The world in plan, baked at edit time: every paved road, every body of water, every town and
    /// every thing worth naming, as flat arrays of world-space XZ.
    ///
    /// <para><b>Why this exists rather than a camera.</b> Two reasons, either fatal on its own.
    /// <c>WorldStreamer</c> disables chunks by distance, so an orthographic camera over the world
    /// photographs a few hundred metres of loaded terrain surrounded by nothing — which is what the
    /// player can already see and is useless as a map. And a second full render pass of the world every
    /// frame does not fit the mobile budget. Drawn from here instead, the map costs one uGUI mesh, is
    /// crisp at any zoom, and knows about ground the player has never been near.</para>
    ///
    /// <para><b>Why not the three baked sources that already exist.</b> <c>TrafficNetwork</c> holds
    /// every drivable road as world-space polylines, <c>WaterHazard</c> holds every water body's spine,
    /// and <c>FillingStations</c> holds every forecourt — but none can be drawn as a map without undoing
    /// what it is for. The traffic routes carry <i>two lanes per street</i> plus a connector for every
    /// legal turn through every junction, so drawn directly they give doubled roads and a spider at each
    /// crossroads; and they carry no names, no water and no features. The other two are a fraction of
    /// the picture. Meanwhile the forty-odd <see cref="RoadFeature"/>s the courses carry — the tunnels,
    /// the viewpoints, the pumps — are baked nowhere at all, so something had to be.</para>
    ///
    /// <para><b>Flat arrays, prefix offsets, and no allocation when read.</b> The same shape
    /// <see cref="TrafficNetwork"/> uses, for the same reason: this is walked every time the minimap
    /// rebuilds its mesh, which is most frames while the car is moving. A <c>ScriptableObject</c>
    /// because it is derived from the layout, exactly like the routes are.</para>
    /// </summary>
    public sealed class WorldMap : ScriptableObject
    {
        [SerializeField] private Vector2[] points;

        [Tooltip("Prefix offsets into the point array: line i owns points[lineStart[i]] up to "
               + "lineStart[i + 1]. One entry longer than the line count.")]
        [SerializeField] private int[] lineStart;

        [Tooltip("What each line is. Bytes because a MapLineKind[] does not serialise.")]
        [SerializeField] private byte[] lineKind;

        [Tooltip("Half-width of each line in metres, so a road draws its true width once the map is "
               + "zoomed in far enough for that to mean anything.")]
        [SerializeField] private float[] lineHalfWidth;

        [Tooltip("The line each point belongs to. One int per point rather than a search: the segment "
               + "grid hands back point indices, and every one of them needs a kind and a width.")]
        [SerializeField] private int[] pointLine;

        [SerializeField] private Vector2[] areaPoints;
        [SerializeField] private int[] areaStart;
        [SerializeField] private byte[] areaKind;
        [SerializeField] private string[] areaName;

        [SerializeField] private Vector2[] markerAt;
        [SerializeField] private byte[] markerKind;
        [SerializeField] private string[] markerName;

        [SerializeField] private Vector2 planMin;
        [SerializeField] private Vector2 planMax;

        // --- The segment grid.
        //
        // Counts to prefix offsets to items, the shape MountainField.BuildBuckets and StreetIndex both
        // use. Items are indices into `points`, and the segment runs from points[i] to points[i + 1] —
        // the last point of a line is never enrolled, so a segment can never straddle two lines.
        //
        // Without this the minimap would walk every segment in the world to find the few hundred within
        // a couple of hundred metres of the car, once per rebuild.

        [SerializeField] private int[] cellStart;
        [SerializeField] private int[] cellItems;
        [SerializeField] private Vector2 gridOrigin;
        [SerializeField] private int columns;
        [SerializeField] private int rows;
        [SerializeField] private float cellSize = 128f;

        public int LineCount => lineStart != null ? Mathf.Max(0, lineStart.Length - 1) : 0;

        public int PointCount => points != null ? points.Length : 0;

        public int AreaCount => areaStart != null ? Mathf.Max(0, areaStart.Length - 1) : 0;

        public int MarkerCount => markerAt != null ? markerAt.Length : 0;

        public int Columns => columns;

        public int Rows => rows;

        public float CellSize => cellSize;

        /// <summary>The world's extent in plan. There is no such constant anywhere else in the project.</summary>
        public Vector2 PlanMin => planMin;

        public Vector2 PlanMax => planMax;

        public Vector2 PlanCentre => (planMin + planMax) * 0.5f;

        public Vector2 PlanSize => planMax - planMin;

        public Vector2 PointAt(int index) => points[index];

        public int LineStartAt(int line) => lineStart[line];

        public int LineEndAt(int line) => lineStart[line + 1];

        public MapLineKind KindOf(int line) => (MapLineKind)lineKind[line];

        public float HalfWidthOf(int line) => lineHalfWidth[line];

        /// <summary>Which line a point belongs to, and therefore what the segment starting there is.</summary>
        public int LineOfPoint(int index) => pointLine[index];

        public Vector2 AreaPointAt(int index) => areaPoints[index];

        public int AreaStartAt(int area) => areaStart[area];

        public int AreaEndAt(int area) => areaStart[area + 1];

        public MapAreaKind AreaKindOf(int area) => (MapAreaKind)areaKind[area];

        public string AreaNameOf(int area) => areaName[area];

        public Vector2 MarkerAt(int index) => markerAt[index];

        public MapMarkerKind MarkerKindOf(int index) => (MapMarkerKind)markerKind[index];

        public string MarkerNameOf(int index) => markerName[index];

        public int ColumnOf(float x) => Mathf.FloorToInt((x - gridOrigin.x) / cellSize);

        public int RowOf(float z) => Mathf.FloorToInt((z - gridOrigin.y) / cellSize);

        /// <summary>
        /// The segments enrolled in one cell, as a half-open range into the item array.
        ///
        /// <para>Out of range asks for nothing rather than clamping. Clamping would fold the world's
        /// outside edge back onto its border cells and draw a strip of road at the horizon.</para>
        /// </summary>
        public void CellRange(int column, int row, out int from, out int to)
        {
            if (cellStart == null || column < 0 || row < 0 || column >= columns || row >= rows)
            {
                from = 0;
                to = 0;
                return;
            }

            int cell = row * columns + column;
            from = cellStart[cell];
            to = cellStart[cell + 1];
        }

        public int ItemAt(int index) => cellItems[index];

        /// <summary>Fills the whole asset. Called once, by <see cref="WorldMapBuilder"/>.</summary>
        public void Fill(
            Vector2[] bakedPoints,
            int[] bakedLineStart,
            byte[] bakedLineKind,
            float[] bakedLineHalfWidth,
            int[] bakedPointLine,
            Vector2[] bakedAreaPoints,
            int[] bakedAreaStart,
            byte[] bakedAreaKind,
            string[] bakedAreaName,
            Vector2[] bakedMarkerAt,
            byte[] bakedMarkerKind,
            string[] bakedMarkerName,
            Vector2 bakedPlanMin,
            Vector2 bakedPlanMax,
            int[] bakedCellStart,
            int[] bakedCellItems,
            Vector2 bakedGridOrigin,
            int bakedColumns,
            int bakedRows,
            float bakedCellSize)
        {
            points = bakedPoints;
            lineStart = bakedLineStart;
            lineKind = bakedLineKind;
            lineHalfWidth = bakedLineHalfWidth;
            pointLine = bakedPointLine;

            areaPoints = bakedAreaPoints;
            areaStart = bakedAreaStart;
            areaKind = bakedAreaKind;
            areaName = bakedAreaName;

            markerAt = bakedMarkerAt;
            markerKind = bakedMarkerKind;
            markerName = bakedMarkerName;

            planMin = bakedPlanMin;
            planMax = bakedPlanMax;

            cellStart = bakedCellStart;
            cellItems = bakedCellItems;
            gridOrigin = bakedGridOrigin;
            columns = bakedColumns;
            rows = bakedRows;
            cellSize = bakedCellSize;
        }
    }
}
