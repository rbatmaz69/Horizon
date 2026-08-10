using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>A junction or a dead end, with its incident edges sorted by the bearing they leave on.</summary>
    public sealed class StreetNode
    {
        public int Index;
        public Vector3 Position;
        public bool OnTrunkRoad;
        public string Name;

        /// <summary>Incident edge indices, sorted by <see cref="Bearings"/>.</summary>
        public int[] Edges;

        /// <summary>
        /// Outgoing bearing of each incident edge, degrees, in the course builder's convention: 0 faces
        /// +Z, increasing turns towards +X. Parallel to <see cref="Edges"/>.
        ///
        /// Sorted order is not a convenience — the junction pad is a polygon walked in bearing order, and
        /// the planar face walk that finds the blocks in the next stage is defined entirely in terms of
        /// "the next edge clockwise".
        /// </summary>
        public float[] Bearings;

        /// <summary>
        /// The junction pad, as three parallel rings filled in by
        /// <see cref="StreetJunctionBuilder.ResolveTrims"/> once the trims are known: the gutter line,
        /// the top of the kerb, and the outer edge of the footway. Null for trunk-road nodes, which get
        /// a throat instead.
        ///
        /// Three rings walked once, rather than an outline each pass would re-derive, so the surface and
        /// the kerbs around it cannot disagree about where the junction is.
        /// </summary>
        public Vector3[] PadGutter;

        public Vector3[] PadKerbTop;

        public Vector3[] PadOutline;

        /// <summary>True where the span from point k to k+1 runs between two streets rather than across
        /// the mouth of one — and therefore gets a kerb.</summary>
        public bool[] PadKerbedAfter;

        public int Degree => Edges != null ? Edges.Length : 0;
    }

    /// <summary>One street between two nodes.</summary>
    public sealed class StreetEdge
    {
        public int Index;
        public int FromNode;
        public int ToNode;
        public TownStreetKind Kind;
        public TownQuarter Quarter;
        public RoadPath Path;
        public TownStreetShape Shape;

        /// <summary>Where the ribbon starts and stops, so the junction pads can fill the rest.</summary>
        public float TrimStart;
        public float TrimEnd;

        public float HalfWidth => Shape.HalfWidth;

        public float HalfOuter => Shape.HalfOuter;

        public float Length => Path != null ? Path.Length : 0f;

        public int Other(int node)
        {
            return node == FromNode ? ToNode : FromNode;
        }

        /// <summary>Distance along this edge of the end that meets <paramref name="node"/>.</summary>
        public float EndDistance(int node)
        {
            return node == FromNode ? 0f : Length;
        }

        /// <summary>Distance along this edge of the trim point at <paramref name="node"/>'s end.</summary>
        public float TrimAt(int node)
        {
            return node == FromNode ? TrimStart : TrimEnd;
        }
    }

    /// <summary>
    /// The town's streets as a graph: nodes where they meet, edges between them, and everything the
    /// mesh builders, the parcelling and the validators read off it.
    ///
    /// <para><b>Nodes are junctions and dead ends only.</b> A street that bends is one edge with a bowed
    /// centreline, never two edges and a node between them — a degree-two node would need a junction pad
    /// shaped like a piece of corridor, and every pad in the place would carry that special case for the
    /// sake of a corner. Anything the layout table produces with degree two is logged rather than
    /// silently drawn, because it means the table meant something the graph cannot say.</para>
    ///
    /// <para><b>The whole network must exist before <see cref="MountainField"/> does.</b> The streets are
    /// what the ground is levelled to; the field is built from their centrelines. Their <i>meshes</i>,
    /// though, can be built before the field, and that is the point of <see cref="TownShape.FloorHeight"/>
    /// — a street's height comes from the same function the level samples come from, so neither has to
    /// wait for the other.</para>
    /// </summary>
    public sealed class StreetNetwork
    {
        private readonly List<StreetNode> nodes;
        private readonly List<StreetEdge> edges;

        private StreetNetwork(List<StreetNode> nodes, List<StreetEdge> edges, Bounds footprint)
        {
            this.nodes = nodes;
            this.edges = edges;
            Footprint = footprint;
        }

        public IReadOnlyList<StreetNode> Nodes => nodes;

        public IReadOnlyList<StreetEdge> Edges => edges;

        /// <summary>Plan bounds of every street centreline, for the terrain corridor and cheap early-outs.</summary>
        public Bounds Footprint { get; }

        /// <summary>Total centreline length, metres.</summary>
        public float TotalLength
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < edges.Count; i++)
                {
                    total += edges[i].Length;
                }

                return total;
            }
        }

        /// <summary>How many nodes meet the trunk road, and therefore get a throat rather than a pad.</summary>
        public int TrunkNodeCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].OnTrunkRoad)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Builds the graph from a layout table: maps every node into world space, lays a
        /// <see cref="RoadPath"/> along every street, and sorts each node's edges by bearing.
        ///
        /// Each edge gets a <see cref="GameObject"/> under <paramref name="parent"/> because
        /// <see cref="RoadPath"/> is a component — which is also what lets the corridor sweep and the
        /// clearance checks treat a town street exactly like the pass.
        /// </summary>
        public static StreetNetwork Build(
            IRoadPath main, in TownShape shape, TownNetworkSpec spec, Transform parent)
        {
            var nodes = new List<StreetNode>(spec.Nodes.Count);
            var edges = new List<StreetEdge>(spec.Streets.Count);

            for (int i = 0; i < spec.Nodes.Count; i++)
            {
                TownNodeSpec node = spec.Nodes[i];
                nodes.Add(new StreetNode
                {
                    Index = i,
                    Position = TownShape.ToWorld(main, shape, node.At.Along, node.At.Across),
                    OnTrunkRoad = node.OnTrunkRoad,
                    Name = node.Name,
                });
            }

            var footprint = new Bounds(nodes.Count > 0 ? nodes[0].Position : Vector3.zero, Vector3.zero);
            var incident = new List<int>[nodes.Count];

            for (int i = 0; i < spec.Streets.Count; i++)
            {
                TownStreetSpec street = spec.Streets[i];
                if (street.From == street.To
                    || street.From < 0 || street.From >= nodes.Count
                    || street.To < 0 || street.To >= nodes.Count)
                {
                    Debug.LogWarning($"[Horizon] Street {i} in the layout table joins nodes "
                                     + $"{street.From} and {street.To}, which is not a street.");
                    continue;
                }

                var edge = new StreetEdge
                {
                    Index = edges.Count,
                    FromNode = street.From,
                    ToNode = street.To,
                    Kind = street.Kind,
                    Quarter = street.Quarter,
                    Shape = TownStreetShape.For(street.Kind),
                };

                edge.Path = BuildPath(main, shape, spec, street, parent, edge.Index, footprintInto: ref footprint);
                if (edge.Path == null)
                {
                    continue;
                }

                edges.Add(edge);

                (incident[street.From] ??= new List<int>(4)).Add(edge.Index);
                (incident[street.To] ??= new List<int>(4)).Add(edge.Index);
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                SortByBearing(nodes[i], incident[i], edges);
            }

            return new StreetNetwork(nodes, edges, footprint);
        }

        /// <summary>
        /// One street's centreline, sampled in town-local coordinates and mapped into the world.
        ///
        /// Working in town-local space all the way to the last step is what makes the height come out
        /// right for free: <see cref="TownShape.ToWorld"/> takes its Y from the same floor function the
        /// ground is levelled to, so a street cannot end up standing on a plinth or buried in a shelf.
        /// Interpolating in world space and asking the height field afterwards would reintroduce exactly
        /// that gap.
        /// </summary>
        private static RoadPath BuildPath(
            IRoadPath main,
            in TownShape shape,
            TownNetworkSpec spec,
            in TownStreetSpec street,
            Transform parent,
            int index,
            ref Bounds footprintInto)
        {
            TownPoint a = spec.Nodes[street.From].At;
            TownPoint b = spec.Nodes[street.To].At;

            float spanAlong = b.Along - a.Along;
            float spanAcross = b.Across - a.Across;
            float span = Mathf.Sqrt(spanAlong * spanAlong + spanAcross * spanAcross);

            if (span < 4f)
            {
                Debug.LogWarning($"[Horizon] Street {index} is {span:0.0} m long, which is a node with "
                                 + "ambitions rather than a street.");
                return null;
            }

            // Perpendicular in town-local space, so the bow is measured in the same coordinates it was
            // authored in and does not change when the trunk road curves.
            float perpAlong = -spanAcross / span;
            float perpAcross = spanAlong / span;

            int steps = Mathf.Max(3, Mathf.CeilToInt(span / 10f));
            var points = new List<Vector3>(steps + 1);

            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;

                // 4t(1-t) peaks at exactly 1 in the middle and is zero at both ends, so Bow is the
                // midpoint offset in metres rather than a number that has to be calibrated.
                float bow = street.Bow * 4f * t * (1f - t);

                float along = Mathf.Lerp(a.Along, b.Along, t) + perpAlong * bow;
                float across = Mathf.Lerp(a.Across, b.Across, t) + perpAcross * bow;

                Vector3 point = TownShape.ToWorld(main, shape, along, across);
                points.Add(point);
                footprintInto.Encapsulate(point);
            }

            var pathObject = new GameObject($"Street_{index}");
            pathObject.transform.SetParent(parent, false);

            RoadPath path = pathObject.AddComponent<RoadPath>();
            path.SetControlPoints(points);
            return path;
        }

        /// <summary>
        /// Sorts a node's incident edges by the bearing they leave on, and records those bearings.
        ///
        /// The bearing is taken a few metres along the edge rather than at the node itself: the first
        /// span of a Catmull-Rom curve through control points is the least reliable place to ask which
        /// way it is going, and a pad polygon built from a bearing that is a couple of degrees out has a
        /// visible notch in it.
        /// </summary>
        private static void SortByBearing(StreetNode node, List<int> incident, List<StreetEdge> edges)
        {
            if (incident == null || incident.Count == 0)
            {
                node.Edges = new int[0];
                node.Bearings = new float[0];
                return;
            }

            int count = incident.Count;
            var bearings = new float[count];

            for (int i = 0; i < count; i++)
            {
                StreetEdge edge = edges[incident[i]];
                bool atStart = edge.FromNode == node.Index;

                float from = atStart ? 0f : edge.Length;
                float towards = atStart ? Mathf.Min(6f, edge.Length) : Mathf.Max(0f, edge.Length - 6f);

                Vector3 direction = edge.Path.GetPositionAtDistance(towards)
                                    - edge.Path.GetPositionAtDistance(from);

                bearings[i] = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }

            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
            }

            System.Array.Sort(bearings, order);

            node.Edges = new int[count];
            node.Bearings = bearings;
            for (int i = 0; i < count; i++)
            {
                node.Edges[i] = incident[order[i]];
            }
        }
    }

    /// <summary>
    /// A uniform grid over the streets, so "how far is this point from a street" is a cell lookup rather
    /// than a walk down every centreline.
    ///
    /// <para>This exists because of a measured cost, not a suspected one. Clearing parcels that stand in
    /// a street was a plot-count times edge-count times samples-per-edge loop — at four hundred parcels
    /// and forty streets that is a million calls to <c>GetPositionAtDistance</c>, each walking an
    /// arc-length table. The pattern is the one <see cref="MountainField"/> already uses for its road
    /// samples: counts, prefix offsets, items.</para>
    /// </summary>
    public sealed class StreetIndex
    {
        private readonly Vector3[] samples;
        private readonly int[] sampleEdge;
        private readonly int[] cellStart;
        private readonly int[] cellItems;
        private readonly Vector2 origin;
        private readonly float cellSize;
        private readonly int columns;
        private readonly int rows;

        public StreetIndex(StreetNetwork network, float sampleSpacing = 4f, float cellSize = 16f)
        {
            this.cellSize = Mathf.Max(4f, cellSize);

            var points = new List<Vector3>(1024);
            var owners = new List<int>(1024);

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];
                int steps = Mathf.Max(2, Mathf.CeilToInt(edge.Length / Mathf.Max(1f, sampleSpacing)));

                for (int step = 0; step <= steps; step++)
                {
                    points.Add(edge.Path.GetPositionAtDistance(edge.Length * step / steps));
                    owners.Add(i);
                }
            }

            samples = points.ToArray();
            sampleEdge = owners.ToArray();

            Bounds bounds = network.Footprint;
            origin = new Vector2(bounds.min.x - this.cellSize, bounds.min.z - this.cellSize);
            columns = Mathf.Max(1, Mathf.CeilToInt((bounds.size.x + this.cellSize * 2f) / this.cellSize));
            rows = Mathf.Max(1, Mathf.CeilToInt((bounds.size.z + this.cellSize * 2f) / this.cellSize));

            BuildBuckets(out cellStart, out cellItems);
        }

        /// <summary>
        /// Plan distance to the nearest street centreline, and which street that was.
        ///
        /// Widening ring search, so a point well outside the town still gets a real answer rather than a
        /// sentinel that callers would have to know about.
        /// </summary>
        public float DistanceTo(float x, float z, out int edgeIndex)
        {
            edgeIndex = -1;
            if (samples.Length == 0)
            {
                return float.MaxValue;
            }

            int centreColumn = Mathf.Clamp(ColumnOf(x), 0, columns - 1);
            int centreRow = Mathf.Clamp(RowOf(z), 0, rows - 1);

            float bestSqr = float.MaxValue;
            int maxRing = Mathf.Max(columns, rows);

            for (int ring = 0; ring <= maxRing; ring++)
            {
                ScanRing(centreColumn, centreRow, ring, x, z, ref bestSqr, ref edgeIndex);

                float reach = ring * cellSize;
                if (bestSqr < float.MaxValue && reach * reach >= bestSqr)
                {
                    break;
                }
            }

            return Mathf.Sqrt(bestSqr);
        }

        /// <summary>
        /// Whether any street runs within <paramref name="distance"/> of a point. Cheaper than
        /// <see cref="DistanceTo"/> — it stops at the first sample close enough and never widens.
        /// </summary>
        public bool IsWithin(float x, float z, float distance)
        {
            int reach = Mathf.Max(1, Mathf.CeilToInt(distance / cellSize));
            float limitSqr = distance * distance;

            int minColumn = Mathf.Max(0, ColumnOf(x) - reach);
            int maxColumn = Mathf.Min(columns - 1, ColumnOf(x) + reach);
            int minRow = Mathf.Max(0, RowOf(z) - reach);
            int maxRow = Mathf.Min(rows - 1, RowOf(z) + reach);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int cell = row * columns + column;
                    for (int slot = cellStart[cell]; slot < cellStart[cell + 1]; slot++)
                    {
                        int index = cellItems[slot];
                        float dx = samples[index].x - x;
                        float dz = samples[index].z - z;

                        if (dx * dx + dz * dz <= limitSqr)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void ScanRing(
            int centreColumn, int centreRow, int ring, float x, float z,
            ref float bestSqr, ref int edgeIndex)
        {
            int minColumn = centreColumn - ring;
            int maxColumn = centreColumn + ring;
            int minRow = centreRow - ring;
            int maxRow = centreRow + ring;

            for (int row = minRow; row <= maxRow; row++)
            {
                if (row < 0 || row >= rows)
                {
                    continue;
                }

                bool edgeRow = row == minRow || row == maxRow;
                int step = edgeRow ? 1 : Mathf.Max(1, maxColumn - minColumn);

                for (int column = minColumn; column <= maxColumn; column += step)
                {
                    if (column < 0 || column >= columns)
                    {
                        continue;
                    }

                    int cell = row * columns + column;
                    for (int slot = cellStart[cell]; slot < cellStart[cell + 1]; slot++)
                    {
                        int index = cellItems[slot];
                        float dx = samples[index].x - x;
                        float dz = samples[index].z - z;
                        float distanceSqr = dx * dx + dz * dz;

                        if (distanceSqr < bestSqr)
                        {
                            bestSqr = distanceSqr;
                            edgeIndex = sampleEdge[index];
                        }
                    }
                }
            }
        }

        private void BuildBuckets(out int[] starts, out int[] items)
        {
            int cellCount = columns * rows;
            var counts = new int[cellCount + 1];

            for (int i = 0; i < samples.Length; i++)
            {
                counts[CellOf(samples[i].x, samples[i].z) + 1]++;
            }

            for (int cell = 0; cell < cellCount; cell++)
            {
                counts[cell + 1] += counts[cell];
            }

            starts = counts;
            items = new int[samples.Length];
            var cursor = new int[cellCount];

            for (int i = 0; i < samples.Length; i++)
            {
                int cell = CellOf(samples[i].x, samples[i].z);
                items[starts[cell] + cursor[cell]] = i;
                cursor[cell]++;
            }
        }

        private int CellOf(float x, float z)
        {
            int column = Mathf.Clamp(ColumnOf(x), 0, columns - 1);
            int row = Mathf.Clamp(RowOf(z), 0, rows - 1);
            return row * columns + column;
        }

        private int ColumnOf(float x)
        {
            return Mathf.FloorToInt((x - origin.x) / cellSize);
        }

        private int RowOf(float z)
        {
            return Mathf.FloorToInt((z - origin.y) / cellSize);
        }
    }
}
