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
        private readonly List<TownSquare> squares;

        private StreetNetwork(
            List<StreetNode> nodes, List<StreetEdge> edges, List<TownSquare> squares, Bounds footprint)
        {
            this.nodes = nodes;
            this.edges = edges;
            this.squares = squares;
            Footprint = footprint;
        }

        public IReadOnlyList<StreetNode> Nodes => nodes;

        public IReadOnlyList<StreetEdge> Edges => edges;

        /// <summary>
        /// The town's squares. Empty until <see cref="BuildSquareInteriors"/> has run, which needs the
        /// trims.
        /// </summary>
        public IReadOnlyList<TownSquare> Squares => squares;

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

            return new StreetNetwork(nodes, edges, BuildSquares(spec, edges), footprint);
        }

        /// <summary>
        /// Resolves each declared square's ring of nodes into the streets between them.
        ///
        /// Only <see cref="TownStreetKind.SquareEdge"/> streets count as edges, which is what lets a
        /// square node also carry ordinary streets leaving it — every square in a real town is somewhere
        /// two roads happen to pass through, and the market square here is threaded onto the route from
        /// the high street to the housing row rather than being a cul-de-sac.
        /// </summary>
        private static List<TownSquare> BuildSquares(TownNetworkSpec spec, List<StreetEdge> edges)
        {
            var squares = new List<TownSquare>(spec.Squares.Count);

            for (int i = 0; i < spec.Squares.Count; i++)
            {
                TownSquareSpec declared = spec.Squares[i];
                if (declared.Nodes == null || declared.Nodes.Length < 3)
                {
                    Debug.LogWarning($"[Horizon] Square '{declared.Name}' has "
                                     + $"{declared.Nodes?.Length ?? 0} nodes, which is not a ring.");
                    continue;
                }

                var square = new TownSquare
                {
                    Index = squares.Count,
                    Name = declared.Name,
                    Nodes = declared.Nodes,
                    Edges = new int[declared.Nodes.Length],
                };

                bool complete = true;

                for (int n = 0; n < declared.Nodes.Length; n++)
                {
                    int a = declared.Nodes[n];
                    int b = declared.Nodes[(n + 1) % declared.Nodes.Length];

                    square.Edges[n] = FindSquareEdge(edges, a, b);
                    if (square.Edges[n] < 0)
                    {
                        complete = false;
                        Debug.LogWarning($"[Horizon] Square '{declared.Name}' names nodes {a} and {b} as "
                                         + "consecutive, but no SquareEdge street joins them. A square's "
                                         + "ring has to be spelled out as streets like any other.");
                    }
                }

                if (complete)
                {
                    squares.Add(square);
                }
            }

            return squares;
        }

        private static int FindSquareEdge(List<StreetEdge> edges, int a, int b)
        {
            for (int i = 0; i < edges.Count; i++)
            {
                StreetEdge edge = edges[i];
                if (edge.Kind != TownStreetKind.SquareEdge)
                {
                    continue;
                }

                if ((edge.FromNode == a && edge.ToNode == b) || (edge.FromNode == b && edge.ToNode == a))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Works out the paved area of every square: the polygon bounded by the *inner* footway edges of
        /// the streets around it.
        ///
        /// <para>Taken along the streets themselves rather than by insetting the ring of node positions,
        /// so the boundary follows every bow exactly and is flush with the pavement it meets. An inset
        /// polygon would be flush only where the streets happen to be straight, and the town's streets
        /// are bowed on purpose.</para>
        ///
        /// <para>Must run after <see cref="StreetJunctionBuilder.ResolveTrims"/>: the boundary runs from
        /// one trim point to the other, because between the trim point and the node the ground belongs to
        /// the junction pad.</para>
        /// </summary>
        public void BuildSquareInteriors()
        {
            for (int s = 0; s < squares.Count; s++)
            {
                TownSquare square = squares[s];

                var rough = Vector3.zero;
                for (int i = 0; i < square.Nodes.Length; i++)
                {
                    rough += nodes[square.Nodes[i]].Position;
                }

                rough /= square.Nodes.Length;

                var points = new List<Vector3>(square.Edges.Length * 8);

                for (int i = 0; i < square.Edges.Length; i++)
                {
                    StreetEdge edge = edges[square.Edges[i]];
                    bool forward = edge.FromNode == square.Nodes[i];

                    float from = forward ? edge.TrimStart : edge.Length - edge.TrimEnd;
                    float to = forward ? edge.Length - edge.TrimEnd : edge.TrimStart;

                    // Which side of this street the square is on, measured rather than assumed: the ring
                    // may be walked either way round and the edges may be stored either way round, so
                    // there are four sign conventions in play and only the dot product knows.
                    float middle = (from + to) * 0.5f;
                    Vector3 right = edge.Path.GetRightAtDistance(middle);
                    Vector3 at = edge.Path.GetPositionAtDistance(middle);
                    float side = Mathf.Sign(Vector3.Dot(right, rough - at));

                    float across = edge.Shape.HalfOuter * side;
                    float rise = edge.Shape.SurfaceLift + edge.Shape.KerbHeight + TownSquare.PaveLift;

                    int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(to - from) / 12f));
                    for (int step = 0; step <= steps; step++)
                    {
                        points.Add(TownStreetBuilder.PointAcross(
                            edge.Path, edge.Shape, Mathf.Lerp(from, to, step / (float)steps), across, rise));
                    }
                }

                // Normalised to the winding a fan from the centre comes out facing up in — the same
                // convention the junction pads walk in, and the same one FindBlocks uses to tell a
                // bounded face from the outside of the town.
                SortAboutCentre(points);
                square.Interior = points.ToArray();
                square.Area = Mathf.Abs(SignedArea(points));
                square.Centre = CentroidOf(square.Interior);
                square.UphillEdge = HighestEdge(square);
            }
        }

        /// <summary>
        /// Which of a square's edges stands highest, and therefore which side of it is uphill.
        ///
        /// The basin's floor rises away from the trunk road, so on this town's square it is the far edge —
        /// but that is a fact about where the square happens to sit rather than a rule, and a town hall
        /// placed by the rule would end up in the wrong place the moment the square moved. Measured off
        /// the finished geometry instead.
        /// </summary>
        private int HighestEdge(TownSquare square)
        {
            int best = 0;
            float bestHeight = float.MinValue;

            for (int i = 0; i < square.Edges.Length; i++)
            {
                StreetEdge edge = edges[square.Edges[i]];
                float height = edge.Path.GetPositionAtDistance(edge.Length * 0.5f).y;

                if (height > bestHeight)
                {
                    bestHeight = height;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Sorts a boundary into strict bearing order about its own middle.
        ///
        /// <para>A no-op for a boundary that is already star-shaped, which four straight-ish streets
        /// produce almost everywhere — but only almost. Where two adjacent streets meet, the boundary
        /// jumps from one street's trim point to the next street's, and at a corner where the bows work
        /// against each other those two points can come out in the wrong order round the middle. The fan
        /// then folds one sliver back through itself, which the flip counter reports and which renders as
        /// a dark wedge in the paving.</para>
        ///
        /// <para>Sorting by bearing makes the fan valid by construction rather than by luck. It is the
        /// right fix rather than a patch over one: a fan from a centre point is *defined* on a
        /// bearing-ordered ring, and building the ring in street order and hoping was the assumption that
        /// did not hold. It also settles the winding, so there is nothing left for a signed area to
        /// correct afterwards.</para>
        /// </summary>
        private static void SortAboutCentre(List<Vector3> outline)
        {
            if (outline.Count < 3)
            {
                return;
            }

            var centre = Vector3.zero;
            for (int i = 0; i < outline.Count; i++)
            {
                centre += outline[i];
            }

            centre /= outline.Count;

            // Ascending, which is clockwise seen from above, which is the walk order the junction pads
            // use and the one a fan from the centre comes out facing up in. Sorted the other way it is
            // 22 flipped triangles, which is how this was settled.
            outline.Sort((a, b) => Bearing(a, centre).CompareTo(Bearing(b, centre)));
        }

        private static float Bearing(Vector3 point, Vector3 centre)
        {
            return Mathf.Atan2(point.x - centre.x, point.z - centre.z);
        }

        private static float SignedArea(List<Vector3> outline)
        {
            float sum = 0f;

            for (int i = 0; i < outline.Count; i++)
            {
                Vector3 a = outline[i];
                Vector3 b = outline[(i + 1) % outline.Count];
                sum += a.x * b.z - b.x * a.z;
            }

            return sum * 0.5f;
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
                MarkSquare(block);
                blocks.Add(block);
            }

            BlocksLieLeftOfWalk = blocks.Count > 0 && LiesLeftOfWalk(blocks[0]);
            return blocks;
        }

        /// <summary>
        /// Decides whether a block is the inside of a square, and drags its neighbours into the market
        /// quarter if it is.
        ///
        /// <para>A face bounded entirely by <see cref="TownStreetKind.SquareEdge"/> streets is a square's
        /// interior. That is the whole test, and it is exact rather than a heuristic — the layout table
        /// only ever uses that street kind to draw a square's ring, so there is nothing else it could
        /// be.</para>
        ///
        /// <para>A block with <i>some</i> square edges on it is the land across the street from the
        /// square, and is forced to <see cref="TownQuarter.Market"/> whatever its other frontages say. A
        /// block whose boundary is one square edge and three housing lanes would otherwise come out
        /// housing on length weighting, which would put back gardens on the market place.</para>
        /// </summary>
        private void MarkSquare(TownBlock block)
        {
            int squareEdges = 0;

            for (int i = 0; i < block.BoundaryEdges.Length; i++)
            {
                if (edges[block.BoundaryEdges[i]].Kind == TownStreetKind.SquareEdge)
                {
                    squareEdges++;
                }
            }

            if (squareEdges == 0)
            {
                return;
            }

            block.IsSquare = squareEdges == block.BoundaryEdges.Length;
            if (!block.IsSquare)
            {
                block.Quarter = TownQuarter.Market;
            }
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

        /// <summary>
        /// True where this face is the inside of a square rather than land to build on.
        ///
        /// The parcelling reads it and lays nothing here, and no frontage faces into it — the buildings
        /// that address a square stand across the street from it, which is what makes the space read as
        /// public rather than as a very large back garden.
        /// </summary>
        public bool IsSquare;
    }

    /// <summary>
    /// A square: the open, paved space inside a ring of streets.
    ///
    /// <para>Its own type rather than a flavour of <see cref="TownBlock"/> because almost nothing about a
    /// block applies to it. It is not parcelled, it has no frontage of its own, its interior is one paved
    /// surface built like a junction pad rather than terrain, and the things standing on it — a fountain,
    /// a row of stalls — are placed against its centroid rather than against a street.</para>
    /// </summary>
    public sealed class TownSquare
    {
        /// <summary>
        /// How far the paving stands above the footways around it, metres.
        ///
        /// Two centimetres, and it is not cosmetic: the junction pad at each corner already paves a wedge
        /// into the square, so the two surfaces overlap by a metre or so at every corner. Coplanar is the
        /// one thing they must not be — that is a z-fighting shimmer across four corners at every angle —
        /// and 2 cm is below what reads as a step at this scale.
        /// </summary>
        public const float PaveLift = 0.02f;

        public int Index;
        public string Name;

        /// <summary>Node indices in ring order, straight off the layout table.</summary>
        public int[] Nodes;

        /// <summary>The street between each consecutive pair of nodes. Parallel to <see cref="Nodes"/>.</summary>
        public int[] Edges;

        /// <summary>The paved boundary, flush with the inner footway of every street around it.</summary>
        public Vector3[] Interior;

        public Vector3 Centre;

        /// <summary>Paved area, square metres.</summary>
        public float Area;

        /// <summary>
        /// Index into <see cref="Edges"/> of the edge standing highest — the uphill side, where the town
        /// hall goes. Measured off the finished geometry, not named in the table.
        /// </summary>
        public int UphillEdge;

        /// <summary>
        /// How high the paving is at a point inside the square.
        ///
        /// <para>The surface is a fan from <see cref="Centre"/> out to <see cref="Interior"/>, so its
        /// height at any point is linear along the ray from the middle to the boundary — and this is that
        /// interpolation, taken against the boundary point nearest in bearing. Not exactly the triangle
        /// the point falls in, which would need the crossing solved; the difference is under a centimetre
        /// on a surface whose whole fall is 0.6 m across eighty metres, and a fountain seated a centimetre
        /// out is not a thing anyone can see. A fountain seated on the <i>terrain</i> under the paving,
        /// which is what asking the height field would give, sinks 16 cm into it.</para>
        /// </summary>
        public float PavedHeightAt(float x, float z)
        {
            if (Interior == null || Interior.Length == 0)
            {
                return Centre.y;
            }

            float dx = x - Centre.x;
            float dz = z - Centre.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            if (distance < 0.01f)
            {
                return Centre.y;
            }

            int best = 0;
            float bestDot = float.MinValue;

            for (int i = 0; i < Interior.Length; i++)
            {
                float ex = Interior[i].x - Centre.x;
                float ez = Interior[i].z - Centre.z;
                float length = Mathf.Sqrt(ex * ex + ez * ez);
                if (length < 0.01f)
                {
                    continue;
                }

                float dot = (dx * ex + dz * ez) / (distance * length);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = i;
                }
            }

            float reach = new Vector2(Interior[best].x - Centre.x, Interior[best].z - Centre.z).magnitude;
            return Mathf.Lerp(Centre.y, Interior[best].y, Mathf.Clamp01(distance / Mathf.Max(0.01f, reach)));
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
