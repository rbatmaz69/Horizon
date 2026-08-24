using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Bakes the world into a <see cref="WorldMap"/>: the paved roads sampled in plan, the town streets,
    /// the water, the town outlines and everything worth a name.
    ///
    /// <para><b>It is handed what it draws; it does not go looking.</b> The world scene holds 199
    /// <c>RoadPath</c> components and only nine of them are roads — <c>MotorwayPath</c> is the median
    /// line the two carriageways are offset from, and <c>SeeburgAxis</c> and <c>ArterialPath</c> are town
    /// axes that exist so <c>TownShape.ToWorld</c> has an along/across frame to map against. Nothing
    /// about a path says which it is. A builder that enumerated the scene would put a road down the
    /// middle of two towns and a third carriageway down the motorway, and the picture is the only place
    /// that would ever show it. So <c>PrototypeSetup</c> passes in the same objects it already holds.
    /// </para>
    ///
    /// <para><b>Everything is sampled, nothing is typed.</b> Every point comes back through
    /// <see cref="IRoadPath.GetPositionAtDistance"/> and every feature through the distance the course
    /// recorded for it — the same rule <c>PrototypeSetup.BuildSpawnTable</c> follows, and for the same
    /// reason: a coordinate written down here would rot the first time a bend was opened out, and it
    /// would rot silently.</para>
    /// </summary>
    public static class WorldMapBuilder
    {
        /// <summary>
        /// Spacing of road samples, metres.
        ///
        /// <para>Twelve rather than the courses' own ten: a hairpin of 20 m radius still comes out as
        /// eight points around its arc, which is round enough at any zoom a map is read at, and the whole
        /// world stays inside a few thousand segments — see the vertex note on <c>MapGraphic</c>.</para>
        /// </summary>
        public const float RoadSampleSpacing = 12f;

        /// <summary>
        /// Side of a bucket in the segment grid, metres.
        ///
        /// <para>Comfortably more than a sample is long, so a segment lands in one or two cells rather
        /// than being smeared across a row of them, and comfortably less than the minimap's own reach,
        /// so looking up two hundred metres of world does not walk a kilometre of cells.</para>
        /// </summary>
        public const float CellSize = 128f;

        /// <summary>How many points a lake or a sea is drawn with. Fixed: a circle is a circle.</summary>
        private const int CircleSegments = 48;

        /// <summary>One paved road, as it is to be drawn.</summary>
        public readonly struct Road
        {
            public readonly IRoadPath Path;
            public readonly MapLineKind Kind;
            public readonly float HalfWidth;

            public Road(IRoadPath path, MapLineKind kind, float halfWidth)
            {
                Path = path;
                Kind = kind;
                HalfWidth = halfWidth;
            }
        }

        /// <summary>
        /// A course and the path its feature distances are measured along.
        ///
        /// <para>Separate from <see cref="Road"/> because the two do not always coincide. The motorway's
        /// features are recorded against its median course, and the median is the one path on that road
        /// which must never be drawn. Ten metres of offset is nothing at map scale; a third carriageway
        /// is not.</para>
        /// </summary>
        public readonly struct Featured
        {
            public readonly IRoadPath Path;
            public readonly RoadCourse Course;

            public Featured(IRoadPath path, RoadCourse course)
            {
                Path = path;
                Course = course;
            }
        }

        /// <summary>A town: its streets, and its name for the label.</summary>
        public readonly struct Town
        {
            public readonly string Name;
            public readonly StreetNetwork Streets;

            public Town(string name, StreetNetwork streets)
            {
                Name = name;
                Streets = streets;
            }
        }

        /// <summary>
        /// One of the places the start screen offers.
        ///
        /// <para>A record of its own rather than <c>SpawnPoint</c>, which lives in <c>Horizon.Game</c> —
        /// the dependencies point one way and this assembly is below that one. The caller converts.</para>
        /// </summary>
        public readonly struct Place
        {
            public readonly string Name;
            public readonly Vector3 Position;

            public Place(string name, Vector3 position)
            {
                Name = name;
                Position = position;
            }
        }

        /// <summary>
        /// How near a feature has to be to a place of the same name before the feature is dropped.
        ///
        /// <para>Passhöhe is a viewpoint on the pass and also one of the ten places, and two symbols on
        /// one spot with the same word beside them reads as a fault in the map rather than as two facts
        /// about the world.</para>
        /// </summary>
        private const float DuplicateReach = 120f;

        public static WorldMap Build(
            IReadOnlyList<Road> roads,
            IReadOnlyList<Featured> featured,
            IReadOnlyList<Town> towns,
            IReadOnlyList<WaterBody> waters,
            IReadOnlyList<Place> places,
            out string report)
        {
            var points = new List<Vector2>(16384);
            var lineStart = new List<int>(256) { 0 };
            var lineKind = new List<byte>(256);
            var lineHalfWidth = new List<float>(256);

            var areaPoints = new List<Vector2>(512);
            var areaStart = new List<int>(16) { 0 };
            var areaKind = new List<byte>(16);
            var areaName = new List<string>(16);

            var markerAt = new List<Vector2>(64);
            var markerKind = new List<byte>(64);
            var markerName = new List<string>(64);

            int streetLines = 0;

            // --- Water first, so it is under everything and so a strait reads as one shape.
            for (int i = 0; i < waters.Count; i++)
            {
                WaterBody body = waters[i];

                // One point in the spine means a lake, more means a corridor. WaterBody's own words.
                if (body.Spine == null || body.Spine.Length == 0)
                {
                    continue;
                }

                if (body.Spine.Length == 1)
                {
                    AddCircle(areaPoints, areaStart, areaKind, areaName,
                        body.Spine[0], body.HalfWidth, MapAreaKind.Water, body.Name);
                    continue;
                }

                AddLine(points, lineStart, lineKind, lineHalfWidth,
                    body.Spine, MapLineKind.River, body.HalfWidth);
            }

            // --- The roads themselves.
            for (int i = 0; i < roads.Count; i++)
            {
                Road road = roads[i];
                if (road.Path == null || road.Path.Length < 1f)
                {
                    continue;
                }

                AddSampledPath(points, lineStart, lineKind, lineHalfWidth,
                    road.Path, 0f, road.Path.Length, road.Kind, road.HalfWidth);
            }

            // --- Town streets, and the town's own outline under them.
            for (int i = 0; i < towns.Count; i++)
            {
                StreetNetwork streets = towns[i].Streets;
                if (streets == null)
                {
                    continue;
                }

                IReadOnlyList<StreetEdge> edges = streets.Edges;
                for (int e = 0; e < edges.Count; e++)
                {
                    StreetEdge edge = edges[e];
                    if (edge.Path == null)
                    {
                        continue;
                    }

                    // Between the trims: the rest of the ribbon is junction pad, which the crossing
                    // streets already draw over.
                    float from = Mathf.Max(0f, edge.TrimStart);
                    float to = Mathf.Min(edge.Length, edge.Length - edge.TrimEnd);

                    if (to - from < 1f)
                    {
                        continue;
                    }

                    AddSampledPath(points, lineStart, lineKind, lineHalfWidth,
                        edge.Path, from, to, MapLineKind.Street, edge.HalfWidth);

                    streetLines++;
                }

                AddTownOutline(areaPoints, areaStart, areaKind, areaName, streets, towns[i].Name);
            }

            // --- Names.
            for (int i = 0; i < places.Count; i++)
            {
                // A place that shares a town's name gets no mark of its own. The town is already an
                // outline with that word across it, and the picture came back with "Seeburg" printed
                // twice over itself — the place at the waterfront and the town at its centroid, a
                // hundred metres apart and illegible at every zoom that showed both.
                if (IsATownName(towns, places[i].Name))
                {
                    continue;
                }

                markerAt.Add(Flat(places[i].Position));
                markerKind.Add((byte)MapMarkerKind.Place);
                markerName.Add(places[i].Name);
            }

            int placeCount = markerAt.Count;

            for (int i = 0; i < featured.Count; i++)
            {
                AddFeatures(markerAt, markerKind, markerName, placeCount, featured[i]);
            }

            // --- Bounds, from what was actually baked rather than from a constant. There is no
            // world-extent constant anywhere in the project, and a wrong one here would crop the map.
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            Encapsulate(points, ref min, ref max);
            Encapsulate(areaPoints, ref min, ref max);
            Encapsulate(markerAt, ref min, ref max);

            if (min.x > max.x)
            {
                min = Vector2.zero;
                max = Vector2.zero;
            }

            // --- The segment grid.
            var pointLine = new int[points.Count];
            for (int line = 0; line < lineKind.Count; line++)
            {
                for (int p = lineStart[line]; p < lineStart[line + 1]; p++)
                {
                    pointLine[p] = line;
                }
            }

            Vector2 gridOrigin = min - new Vector2(CellSize, CellSize);
            int columns = Mathf.Max(1, Mathf.CeilToInt((max.x - gridOrigin.x) / CellSize) + 1);
            int rows = Mathf.Max(1, Mathf.CeilToInt((max.y - gridOrigin.y) / CellSize) + 1);

            BuildBuckets(points, lineStart, gridOrigin, columns, rows,
                out int[] cellStart, out int[] cellItems);

            var map = ScriptableObject.CreateInstance<WorldMap>();

            map.Fill(
                points.ToArray(), lineStart.ToArray(), lineKind.ToArray(), lineHalfWidth.ToArray(),
                pointLine,
                areaPoints.ToArray(), areaStart.ToArray(), areaKind.ToArray(), areaName.ToArray(),
                markerAt.ToArray(), markerKind.ToArray(), markerName.ToArray(),
                min, max,
                cellStart, cellItems, gridOrigin, columns, rows, CellSize);

            var text = new StringBuilder();
            text.Append($" {lineKind.Count} lines ({streetLines} streets), {points.Count} points,");
            text.Append($" {areaKind.Count} areas, {markerAt.Count} markers,");
            text.Append($" {columns}x{rows} cells over {max.x - min.x:0} x {max.y - min.y:0} m.");
            report = text.ToString();

            return map;
        }

        private static bool IsATownName(IReadOnlyList<Town> towns, string name)
        {
            for (int i = 0; i < towns.Count; i++)
            {
                if (towns[i].Name == name)
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector2 Flat(Vector3 position)
        {
            return new Vector2(position.x, position.z);
        }

        private static void Encapsulate(List<Vector2> from, ref Vector2 min, ref Vector2 max)
        {
            for (int i = 0; i < from.Count; i++)
            {
                min = Vector2.Min(min, from[i]);
                max = Vector2.Max(max, from[i]);
            }
        }

        /// <summary>Samples a stretch of a path by arc length and adds it as one line.</summary>
        private static void AddSampledPath(
            List<Vector2> points, List<int> lineStart, List<byte> lineKind, List<float> lineHalfWidth,
            IRoadPath path, float from, float to, MapLineKind kind, float halfWidth)
        {
            float span = to - from;
            int count = Mathf.Max(2, Mathf.CeilToInt(span / RoadSampleSpacing) + 1);

            for (int i = 0; i < count; i++)
            {
                float distance = from + span * (i / (float)(count - 1));
                points.Add(Flat(path.GetPositionAtDistance(distance)));
            }

            lineStart.Add(points.Count);
            lineKind.Add((byte)kind);
            lineHalfWidth.Add(halfWidth);
        }

        private static void AddLine(
            List<Vector2> points, List<int> lineStart, List<byte> lineKind, List<float> lineHalfWidth,
            Vector2[] spine, MapLineKind kind, float halfWidth)
        {
            for (int i = 0; i < spine.Length; i++)
            {
                points.Add(spine[i]);
            }

            lineStart.Add(points.Count);
            lineKind.Add((byte)kind);
            lineHalfWidth.Add(halfWidth);
        }

        private static void AddCircle(
            List<Vector2> areaPoints, List<int> areaStart, List<byte> areaKind, List<string> areaName,
            Vector2 centre, float radius, MapAreaKind kind, string name)
        {
            for (int i = 0; i < CircleSegments; i++)
            {
                float angle = i / (float)CircleSegments * Mathf.PI * 2f;
                areaPoints.Add(centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
            }

            areaStart.Add(areaPoints.Count);
            areaKind.Add((byte)kind);
            areaName.Add(name);
        }

        /// <summary>
        /// A town's outline as the convex hull of its street junctions.
        ///
        /// <para>Rather than <c>StreetNetwork.Footprint</c>, which is an axis-aligned box: no town in
        /// this world is square to the world axes — each one hangs off the road through it — so a box
        /// reads as a town roughly twice the size of the one whose streets are drawn inside it. A hull
        /// is convex, which is what lets the graphic fill it with a triangle fan.</para>
        /// </summary>
        private static void AddTownOutline(
            List<Vector2> areaPoints, List<int> areaStart, List<byte> areaKind, List<string> areaName,
            StreetNetwork streets, string name)
        {
            IReadOnlyList<StreetNode> nodes = streets.Nodes;
            if (nodes.Count < 3)
            {
                return;
            }

            var plan = new List<Vector2>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                plan.Add(Flat(nodes[i].Position));
            }

            List<Vector2> hull = ConvexHull(plan);
            if (hull.Count < 3)
            {
                return;
            }

            for (int i = 0; i < hull.Count; i++)
            {
                areaPoints.Add(hull[i]);
            }

            areaStart.Add(areaPoints.Count);
            areaKind.Add((byte)MapAreaKind.Town);
            areaName.Add(name);
        }

        /// <summary>Monotone chain, counter-clockwise. Sorts its input in place.</summary>
        private static List<Vector2> ConvexHull(List<Vector2> plan)
        {
            plan.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));

            var hull = new List<Vector2>(plan.Count * 2);

            for (int i = 0; i < plan.Count; i++)
            {
                while (hull.Count >= 2
                       && Cross(hull[hull.Count - 2], hull[hull.Count - 1], plan[i]) <= 0f)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(plan[i]);
            }

            // The upper chain may not eat into the lower one, which is what the floor is for.
            int floor = hull.Count + 1;

            for (int i = plan.Count - 2; i >= 0; i--)
            {
                while (hull.Count >= floor
                       && Cross(hull[hull.Count - 2], hull[hull.Count - 1], plan[i]) <= 0f)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(plan[i]);
            }

            // The walk closes on the point it started from.
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static float Cross(Vector2 origin, Vector2 a, Vector2 b)
        {
            return (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x);
        }

        /// <summary>
        /// Turns a course's features into markers, positioned by feeding their own distances back
        /// through the path they were measured along.
        ///
        /// <para>Galleries, villages and junctions are deliberately left out. The first two are already
        /// on the map — a village as a town outline, a gallery as the road it roofs — and a fork is not
        /// a destination.</para>
        /// </summary>
        private static void AddFeatures(
            List<Vector2> markerAt, List<byte> markerKind, List<string> markerName,
            int placeCount, in Featured featured)
        {
            if (featured.Path == null || featured.Course == null)
            {
                return;
            }

            IReadOnlyList<RoadFeature> features = featured.Course.Features;

            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];
                MapMarkerKind kind;

                switch (feature.Kind)
                {
                    case RoadFeatureKind.FuelStation:
                        kind = MapMarkerKind.FuelStation;
                        break;
                    case RoadFeatureKind.Viewpoint:
                        kind = MapMarkerKind.Viewpoint;
                        break;
                    case RoadFeatureKind.Tunnel:
                        kind = MapMarkerKind.Tunnel;
                        break;
                    case RoadFeatureKind.Bridge:
                    case RoadFeatureKind.Suspension:
                        kind = MapMarkerKind.Bridge;
                        break;
                    default:
                        continue;
                }

                float at = Mathf.Clamp(
                    (feature.StartDistance + feature.EndDistance) * 0.5f, 0f, featured.Path.Length);

                Vector2 where = Flat(featured.Path.GetPositionAtDistance(at));

                if (IsAlreadyAPlace(markerAt, markerName, placeCount, where, feature.Name))
                {
                    continue;
                }

                markerAt.Add(where);
                markerKind.Add((byte)kind);
                markerName.Add(feature.Name);
            }
        }

        private static bool IsAlreadyAPlace(
            List<Vector2> markerAt, List<string> markerName, int placeCount, Vector2 where, string name)
        {
            for (int i = 0; i < placeCount; i++)
            {
                if (markerName[i] == name
                    && (markerAt[i] - where).sqrMagnitude < DuplicateReach * DuplicateReach)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Counts, prefix offsets, items — the shape <c>MountainField.BuildBuckets</c> and
        /// <c>StreetIndex</c> both use.
        ///
        /// <para>An item is the index of a segment's <i>first</i> point, and the last point of a line is
        /// never enrolled, so following an item to <c>i + 1</c> can never step onto the next line. A
        /// segment is registered in every cell its own bounds touch, which at this sampling and this cell
        /// size is one or two.</para>
        /// </summary>
        private static void BuildBuckets(
            List<Vector2> points, List<int> lineStart, Vector2 origin, int columns, int rows,
            out int[] starts, out int[] items)
        {
            int cellCount = columns * rows;
            var counts = new int[cellCount + 1];
            int enrolled = 0;

            // Pass one: how many segments each cell holds.
            for (int line = 0; line + 1 < lineStart.Count; line++)
            {
                for (int p = lineStart[line]; p + 1 < lineStart[line + 1]; p++)
                {
                    Span(points[p], points[p + 1], origin, columns, rows,
                        out int fromColumn, out int toColumn, out int fromRow, out int toRow);

                    for (int row = fromRow; row <= toRow; row++)
                    {
                        for (int column = fromColumn; column <= toColumn; column++)
                        {
                            counts[row * columns + column + 1]++;
                            enrolled++;
                        }
                    }
                }
            }

            for (int cell = 0; cell < cellCount; cell++)
            {
                counts[cell + 1] += counts[cell];
            }

            starts = counts;
            items = new int[enrolled];
            var cursor = new int[cellCount];

            // Pass two: fill.
            for (int line = 0; line + 1 < lineStart.Count; line++)
            {
                for (int p = lineStart[line]; p + 1 < lineStart[line + 1]; p++)
                {
                    Span(points[p], points[p + 1], origin, columns, rows,
                        out int fromColumn, out int toColumn, out int fromRow, out int toRow);

                    for (int row = fromRow; row <= toRow; row++)
                    {
                        for (int column = fromColumn; column <= toColumn; column++)
                        {
                            int cell = row * columns + column;
                            items[starts[cell] + cursor[cell]] = p;
                            cursor[cell]++;
                        }
                    }
                }
            }
        }

        /// <summary>The block of cells one segment's bounds cover.</summary>
        private static void Span(
            Vector2 a, Vector2 b, Vector2 origin, int columns, int rows,
            out int fromColumn, out int toColumn, out int fromRow, out int toRow)
        {
            fromColumn = Column(Mathf.Min(a.x, b.x), origin.x, columns);
            toColumn = Column(Mathf.Max(a.x, b.x), origin.x, columns);
            fromRow = Column(Mathf.Min(a.y, b.y), origin.y, rows);
            toRow = Column(Mathf.Max(a.y, b.y), origin.y, rows);
        }

        private static int Column(float value, float origin, int count)
        {
            return Mathf.Clamp(Mathf.FloorToInt((value - origin) / CellSize), 0, count - 1);
        }
    }
}
