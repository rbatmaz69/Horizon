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
            IRoadPath main, in TownShape shape, TownNetworkSpec spec, Transform parent, float shelfDrop)
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
                    Shape = TownStreetShape.For(street.Kind, shelfDrop),
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
        /// The blocks: the bounded faces of the graph, found by walking half-edges.
        ///
        /// <para>The network is planar — <c>ValidateStreetNetwork</c> checks that, and this depends on it
        /// — so its blocks are exactly its faces, and a face walk finds them exactly rather than
        /// approximately. No flood fill, no rasterisation, no tolerance to tune: two directed half-edges
        /// per street, and from each one the next is the one that leaves its far node next round in
        /// bearing order. Every closed walk is a face; the one whose signed area comes out the other way
        /// up is the outside of the town.</para>
        ///
        /// <para>A street with nothing on the far side of it — a dead-end spur — is a bridge, and the
        /// outer walk passes down both its sides. That is correct rather than a special case: it has
        /// frontage on both sides and no block behind either.</para>
        /// </summary>
        public List<TownBlock> FindBlocks(out int[] blockOfHalfEdge)
        {
            int halfEdges = edges.Count * 2;
            blockOfHalfEdge = new int[halfEdges];
            for (int i = 0; i < halfEdges; i++)
            {
                blockOfHalfEdge[i] = -1;
            }

            var walks = new List<List<int>>();
            var seen = new bool[halfEdges];

            for (int start = 0; start < halfEdges; start++)
            {
                if (seen[start])
                {
                    continue;
                }

                var walk = new List<int>(8);
                int half = start;

                // The guard is not paranoia: a bearing list that is not sorted, or a node whose incident
                // list disagrees with its edges, turns this into an infinite loop rather than a wrong
                // answer, and it would do so inside a menu command with no way out.
                for (int step = 0; step < halfEdges + 1; step++)
                {
                    if (seen[half])
                    {
                        break;
                    }

                    seen[half] = true;
                    walk.Add(half);
                    half = NextHalfEdge(half);
                }

                walks.Add(walk);
            }

            var blocks = new List<TownBlock>(walks.Count);

            for (int i = 0; i < walks.Count; i++)
            {
                List<int> walk = walks[i];
                if (walk.Count < 3)
                {
                    continue;
                }

                Vector3[] outline = OutlineOf(walk);
                float signed = SignedArea(outline);

                // The outer face is the one wound the other way. Every bounded face of a planar graph
                // shares a winding; the unbounded one cannot.
                if (signed >= 0f)
                {
                    continue;
                }

                var block = new TownBlock
                {
                    Index = blocks.Count,
                    BoundaryEdges = new int[walk.Count],
                    BoundaryForward = new bool[walk.Count],
                    Outline = outline,
                    Area = Mathf.Abs(signed),
                    Centroid = CentroidOf(outline),
                };

                for (int j = 0; j < walk.Count; j++)
                {
                    block.BoundaryEdges[j] = walk[j] >> 1;
                    block.BoundaryForward[j] = (walk[j] & 1) == 0;
                    blockOfHalfEdge[walk[j]] = block.Index;
                }

                block.Quarter = QuarterOf(block);
                blocks.Add(block);
            }

            BlocksLieLeftOfWalk = blocks.Count > 0 && LiesLeftOfWalk(blocks[0]);
            return blocks;
        }

        /// <summary>
        /// Which side of a boundary street its own block is on.
        ///
        /// Every bounded face of a planar graph is walked the same way round, so this is one property of
        /// the whole network rather than one per edge — but it depends on a winding convention nobody
        /// should have to hold in their head, so it is measured once instead: offset a metre from the
        /// middle of one boundary street and ask whether that point is inside the block.
        /// </summary>
        public bool BlocksLieLeftOfWalk { get; private set; }

        private bool LiesLeftOfWalk(TownBlock block)
        {
            StreetEdge edge = edges[block.BoundaryEdges[0]];
            bool forward = block.BoundaryForward[0];

            float middle = edge.Length * 0.5f;
            Vector3 at = edge.Path.GetPositionAtDistance(middle);
            Vector3 right = edge.Path.GetRightAtDistance(middle) * (forward ? 1f : -1f);

            return Contains(block.Outline, at - right * 3f);
        }

        /// <summary>Ray-cast point-in-polygon test in plan.</summary>
        public static bool Contains(Vector3[] outline, Vector3 point)
        {
            bool inside = false;

            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                if (outline[i].z > point.z != outline[j].z > point.z
                    && point.x < (outline[j].x - outline[i].x) * (point.z - outline[i].z)
                        / (outline[j].z - outline[i].z) + outline[i].x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// The next half-edge round a face: from the far node of this one, the street that leaves next in
        /// bearing order after the one we arrived on.
        /// </summary>
        private int NextHalfEdge(int half)
        {
            StreetEdge edge = edges[half >> 1];
            int arrivedAt = (half & 1) == 0 ? edge.ToNode : edge.FromNode;

            StreetNode node = nodes[arrivedAt];
            int slot = System.Array.IndexOf(node.Edges, edge.Index);
            if (slot < 0)
            {
                return half ^ 1;
            }

            // One round *back* in bearing order, not forward. Forward walks the faces the other way and
            // hands back the outside of the town as a single ring with everything inside it.
            int nextSlot = (slot - 1 + node.Degree) % node.Degree;
            StreetEdge nextEdge = edges[node.Edges[nextSlot]];

            // Directed away from the node we are standing on.
            return (nextEdge.Index << 1) | (nextEdge.FromNode == arrivedAt ? 0 : 1);
        }

        /// <summary>
        /// A walk's boundary as a polygon, sampling each street along its length rather than joining the
        /// nodes with straight lines. A bowed street is most of what gives a block its shape, and a
        /// centroid taken from the corners alone lands outside a crescent.
        /// </summary>
        private Vector3[] OutlineOf(List<int> walk)
        {
            var points = new List<Vector3>(walk.Count * 5);

            for (int i = 0; i < walk.Count; i++)
            {
                StreetEdge edge = edges[walk[i] >> 1];
                bool forward = (walk[i] & 1) == 0;

                int steps = Mathf.Max(2, Mathf.CeilToInt(edge.Length / 20f));
                for (int step = 0; step < steps; step++)
                {
                    float t = step / (float)steps;
                    points.Add(edge.Path.GetPositionAtDistance(edge.Length * (forward ? t : 1f - t)));
                }
            }

            return points.ToArray();
        }

        private static float SignedArea(Vector3[] outline)
        {
            float sum = 0f;

            for (int i = 0; i < outline.Length; i++)
            {
                Vector3 a = outline[i];
                Vector3 b = outline[(i + 1) % outline.Length];
                sum += a.x * b.z - b.x * a.z;
            }

            return sum * 0.5f;
        }

        private static Vector3 CentroidOf(Vector3[] outline)
        {
            var sum = Vector3.zero;
            for (int i = 0; i < outline.Length; i++)
            {
                sum += outline[i];
            }

            return sum / outline.Length;
        }

        /// <summary>
        /// A block's quarter: whichever its boundary streets mostly belong to, weighted by how much of
        /// the boundary each is.
        ///
        /// From the table rather than from a roll, so what a quarter is stays readable in a diff. Length
        /// weighting rather than a straight count, because a block bounded by three short alleys and one
        /// long high street frontage is on the high street.
        /// </summary>
        private TownQuarter QuarterOf(TownBlock block)
        {
            var weight = new float[5];

            for (int i = 0; i < block.BoundaryEdges.Length; i++)
            {
                StreetEdge edge = edges[block.BoundaryEdges[i]];
                weight[(int)edge.Quarter] += edge.Length;
            }

            int best = 0;
            for (int i = 1; i < weight.Length; i++)
            {
                if (weight[i] > weight[best])
                {
                    best = i;
                }
            }

            return (TownQuarter)best;
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
    /// One bounded face of the street graph: the land a ring of streets encloses.
    ///
    /// A block is what a parcel belongs to, and it is what makes the difference between houses ranged
    /// along a street and a town: it is the thing that has a depth, a quarter, and two sides that have to
    /// share it.
    /// </summary>
    public sealed class TownBlock
    {
        public int Index;

        /// <summary>Edges round the boundary, in walk order.</summary>
        public int[] BoundaryEdges;

        /// <summary>True where the walk ran from the edge's <c>FromNode</c> to its <c>ToNode</c>.</summary>
        public bool[] BoundaryForward;

        public Vector3 Centroid;

        /// <summary>Plan area, square metres. Always positive.</summary>
        public float Area;

        /// <summary>The boundary as a polygon, following the streets' curves rather than cutting corners.</summary>
        public Vector3[] Outline;

        public TownQuarter Quarter;
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
