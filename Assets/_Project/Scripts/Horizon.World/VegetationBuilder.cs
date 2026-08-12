using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>What a vegetation pass produced. Used for the build log and for the clearance check.</summary>
    public sealed class VegetationStats
    {
        public int Conifers;
        public int Broadleaves;
        public int Shrubs;
        public int Tufts;
        public int Boulders;
        public int Snags;
        public int Triangles;

        /// <summary>
        /// Faces the buffer had to turn round. Must be zero — see <c>TownStats.Flips</c>.
        ///
        /// The plant builders wind by hand rather than by hint (their rings are jittered too far for an
        /// outward direction to be trustworthy), so this only ever counts geometry that came in through a
        /// facing helper. It is here so the number is reported for every mesh in the world, not only some.
        /// </summary>
        public int Flips;

        /// <summary>Closest any plant came to the centreline. Nothing should ever be on the asphalt.</summary>
        public float ClosestToRoad = float.MaxValue;

        /// <summary>
        /// How far the nearest plant came to the *paved* edge of a town street, metres. Negative means
        /// something is standing on one.
        ///
        /// A separate number from <see cref="ClosestToRoad"/>, which measures the trunk road, because
        /// that is exactly the gap trees grew through: the height field's road distance answers for the
        /// pass and knows nothing about the town's thirty-four streets, and nothing else was watching.
        /// The corridor sweep would not have caught it either — it asks physics, and a plant has no
        /// collider.
        /// </summary>
        public float ClosestToStreet = float.MaxValue;

        /// <summary>Which of the <see cref="PlantMeshes"/> submeshes the finished mesh actually contains.</summary>
        public readonly List<int> Submeshes = new List<int>(PlantMeshes.SubmeshCount);

        public int Plants => Conifers + Broadleaves + Shrubs + Tufts + Boulders + Snags;

        public void Add(VegetationStats other)
        {
            Conifers += other.Conifers;
            Broadleaves += other.Broadleaves;
            Shrubs += other.Shrubs;
            Tufts += other.Tufts;
            Boulders += other.Boulders;
            Snags += other.Snags;
            Triangles += other.Triangles;
            Flips += other.Flips;
            ClosestToRoad = Mathf.Min(ClosestToRoad, other.ClosestToRoad);
            ClosestToStreet = Mathf.Min(ClosestToStreet, other.ClosestToStreet);
        }
    }

    /// <summary>
    /// What every tile needs to know but none of them should work out for itself: the elevation range of the
    /// climb, and the places nothing may grow.
    ///
    /// The keep-out list is the interesting part. A tunnel in this world is a closed body of rock standing on
    /// untouched ground — the height field deliberately knows nothing about it (see <see cref="MountainField"/>)
    /// — so <c>HeightAt</c> under a bore returns the open hillside, and a scatter that trusted it would grow a
    /// forest inside the mountain. There is also no query anywhere in the codebase that turns a world position
    /// back into a distance along the road, so it cannot be asked the other way round either.
    ///
    /// What works instead is to walk the covered stretches once, keep their centreline points, and reject any
    /// candidate that lands near one. A hundred points for the whole pass, tested only after a bounding-box
    /// rejection, which almost every candidate fails immediately.
    /// </summary>
    public sealed class VegetationContext
    {
        /// <summary>Spacing of the stored centreline points along a covered stretch, metres.</summary>
        private const float BlockerSpacing = 4f;

        private readonly Vector3[] blockers;
        private readonly Vector3[] viewpoints;
        private readonly float blockerRadius;
        private readonly float viewpointRadius;

        /// <summary>
        /// Every settlement in the world, each with its own streets, squares and plots.
        ///
        /// <para>An array rather than a single town because the keep-outs are the one thing a second
        /// settlement cannot share. The scatter runs per terrain tile over the whole world, and a
        /// context that knew about one town would grow a forest through the other one — silently, since
        /// nothing else in the build looks at where a tree ended up.</para>
        /// </summary>
        private readonly TownKeepOut[] towns;


        /// <summary>
        /// The squares' paved outlines, so nothing grows out of the market place.
        ///
        /// <para>A polygon rather than a centre and a radius, unlike the junctions above, because a square
        /// is eighty metres by forty and a circle would either leave trees on the flagstones or clear a
        /// disc out of the buildings around it. And it cannot be left to the street keep-out at all: that
        /// measures distance to the nearest centreline, and the middle of a square is twenty metres from
        /// every street that bounds it — which is to say, as far from a street as a back garden is.</para>
        /// </summary>

        // Not readonly: Encapsulate widens them, and a readonly field cannot be assigned from a helper.
        private float minX;
        private float maxX;
        private float minZ;
        private float maxZ;

        private readonly bool hasBlockers;

        /// <summary>One settlement handed to the scatter: what stands in it and what it is paved with.</summary>
        public readonly struct TownSource
        {
            public readonly TownPlan Plan;
            public readonly StreetNetwork Network;
            public readonly float PlotClearance;
            public readonly float TreeKeepOut;

            public TownSource(TownPlan plan, StreetNetwork network, float plotClearance, float treeKeepOut)
            {
                Plan = plan;
                Network = network;
                PlotClearance = plotClearance;
                TreeKeepOut = treeKeepOut;
            }
        }

        /// <summary>One settlement's keep-outs, worked out once and then asked per plant candidate.</summary>
        private sealed class TownKeepOut
        {
            public TownPlan Plan;
            public float PlotClearance;
            public float TreeKeepOut;

            /// <summary>
            /// The streets, so nothing grows on one.
            ///
            /// <para>Needed because <see cref="MountainField.DistanceToRoad"/> answers for the <i>trunk
            /// road</i> and nothing else — which is right, and which leaves every street in a town with
            /// no keep-out at all. Trees and bushes came up through the carriageway, and none of the
            /// checks noticed: the corridor sweep asks physics, and a plant has no collider.</para>
            /// </summary>
            public StreetIndex Streets;

            public IReadOnlyList<StreetEdge> Edges;

            /// <summary>Junction centres and their pads' reach, since a pad is wider than its streets.</summary>
            public Vector3[] Junctions;

            public float[] JunctionRadius;

            /// <summary>
            /// The squares' paved outlines, so nothing grows out of the market place.
            ///
            /// <para>A polygon rather than a centre and a radius, unlike the junctions, because a square
            /// is eighty metres by forty and a circle would either leave trees on the flagstones or clear
            /// a disc out of the buildings around it. And it cannot be left to the street keep-out: that
            /// measures distance to the nearest centreline, and the middle of a square is as far from a
            /// street as a back garden is.</para>
            /// </summary>
            public Vector3[][] SquareOutlines;

            public Bounds[] SquareBounds;
            public Bounds Bounds;
            public bool HasStreets;
        }

        public VegetationContext(
            IRoadPath path,
            RoadCourse course,
            in VegetationShape shape,
            IReadOnlyList<TownSource> settlements = null)
        {
            blockerRadius = shape.TunnelExclusion;
            viewpointRadius = shape.ViewpointClearing;

            int count = settlements != null ? settlements.Count : 0;
            towns = new TownKeepOut[count];

            for (int s = 0; s < count; s++)
            {
                TownSource source = settlements[s];
                StreetNetwork network = source.Network;

                var keep = new TownKeepOut
                {
                    Plan = source.Plan,
                    PlotClearance = source.PlotClearance,
                    TreeKeepOut = source.TreeKeepOut,
                };

                towns[s] = keep;

                if (network == null || network.Edges.Count == 0)
                {
                    continue;
                }

                keep.Streets = new StreetIndex(network, 4f, 16f);
                keep.Edges = network.Edges;

                var centres = new List<Vector3>(network.Nodes.Count);
                var radii = new List<float>(network.Nodes.Count);

                for (int i = 0; i < network.Nodes.Count; i++)
                {
                    StreetNode node = network.Nodes[i];
                    if (node.PadOutline == null || node.PadOutline.Length == 0)
                    {
                        continue;
                    }

                    // The pad's own reach, taken from the outline rather than guessed from the widest
                    // street: a junction of two wide streets at a shallow angle is trimmed a long way
                    // back, and a fixed radius would either leave shrubs on the tarmac or clear a hole
                    // in the grass twenty metres across.
                    float reach = 0f;
                    for (int q = 0; q < node.PadOutline.Length; q++)
                    {
                        Vector3 offset = node.PadOutline[q] - node.Position;
                        offset.y = 0f;
                        reach = Mathf.Max(reach, offset.magnitude);
                    }

                    centres.Add(node.Position);
                    radii.Add(reach);
                }

                keep.Junctions = centres.ToArray();
                keep.JunctionRadius = radii.ToArray();

                var outlines = new List<Vector3[]>(network.Squares.Count);
                var squareBoxes = new List<Bounds>(network.Squares.Count);

                for (int i = 0; i < network.Squares.Count; i++)
                {
                    Vector3[] interior = network.Squares[i].Interior;
                    if (interior == null || interior.Length < 3)
                    {
                        continue;
                    }

                    var box = new Bounds(interior[0], Vector3.zero);
                    for (int q = 1; q < interior.Length; q++)
                    {
                        box.Encapsulate(interior[q]);
                    }

                    outlines.Add(interior);
                    squareBoxes.Add(box);
                }

                keep.SquareOutlines = outlines.ToArray();
                keep.SquareBounds = squareBoxes.ToArray();

                keep.Bounds = network.Footprint;
                keep.Bounds.Expand(new Vector3(60f, 0f, 60f));
                keep.HasStreets = true;
            }

            var covered = new List<Vector3>(128);
            var views = new List<Vector3>(8);

            if (course != null)
            {
                for (int i = 0; i < course.Features.Count; i++)
                {
                    RoadFeature feature = course.Features[i];

                    if (feature.Kind == RoadFeatureKind.Viewpoint)
                    {
                        views.Add(path.GetPositionAtDistance(feature.StartDistance));
                        continue;
                    }

                    // A town is not a tunnel. Left to the branch below it would become a 58 m capsule
                    // that blocks every species including grass, and a settlement standing on bare earth
                    // looks abandoned rather than lived-in. It gets its own two rules instead: nothing at
                    // all on a plot, no trees over the town as a whole, grass and bushes everywhere.
                    if (feature.Kind == RoadFeatureKind.Village)
                    {
                        continue;
                    }

                    float from = feature.StartDistance - shape.TunnelEndMargin;
                    float to = feature.EndDistance + shape.TunnelEndMargin;
                    int steps = Mathf.Max(2, Mathf.CeilToInt((to - from) / BlockerSpacing) + 1);

                    for (int step = 0; step < steps; step++)
                    {
                        float distance = Mathf.Lerp(from, to, step / (float)(steps - 1));
                        covered.Add(path.GetPositionAtDistance(Mathf.Clamp(distance, 0f, path.Length)));
                    }
                }
            }

            blockers = covered.ToArray();
            viewpoints = views.ToArray();

            LowestElevation = course != null ? course.LowestElevation : 0f;
            SummitElevation = course != null ? course.Summit.y : LowestElevation + 1f;

            hasBlockers = blockers.Length > 0 || viewpoints.Length > 0;
            if (!hasBlockers)
            {
                return;
            }

            minX = float.MaxValue;
            maxX = float.MinValue;
            minZ = float.MaxValue;
            maxZ = float.MinValue;

            Encapsulate(blockers, blockerRadius);
            Encapsulate(viewpoints, viewpointRadius);
        }

        public float LowestElevation { get; }

        public float SummitElevation { get; }

        /// <summary>
        /// How far a point is clear of the nearest town street's paved edge, metres. Negative means it is
        /// standing on the street; <see cref="float.MaxValue"/> means there is no town near it.
        /// </summary>
        public float PavedMargin(float x, float z)
        {
            float margin = float.MaxValue;

            for (int s = 0; s < towns.Length; s++)
            {
                TownKeepOut town = towns[s];

                if (!town.HasStreets || x < town.Bounds.min.x || x > town.Bounds.max.x
                    || z < town.Bounds.min.z || z > town.Bounds.max.z)
                {
                    continue;
                }

                float toStreet = town.Streets.DistanceTo(x, z, out int edgeIndex);
                if (edgeIndex >= 0)
                {
                    margin = Mathf.Min(margin, toStreet - town.Edges[edgeIndex].HalfOuter);
                }

                for (int i = 0; i < town.Junctions.Length; i++)
                {
                    float dx = town.Junctions[i].x - x;
                    float dz = town.Junctions[i].z - z;
                    margin = Mathf.Min(margin, Mathf.Sqrt(dx * dx + dz * dz) - town.JunctionRadius[i]);
                }

                // Standing on a square is standing on paving, and that is all this needs to say. How far
                // *into* the square it is would cost a distance-to-polygon and is a number about a plant
                // that is not there — IsBlocked keeps them all out.
                if (IsOnPaving(town, x, z))
                {
                    margin = Mathf.Min(margin, -1f);
                }
            }

            return margin;
        }

        /// <summary>Whether a point falls inside any square's paved outline.</summary>
        private static bool IsOnPaving(TownKeepOut town, float x, float z)
        {
            if (town.SquareOutlines == null)
            {
                return false;
            }

            var at = new Vector3(x, 0f, z);

            for (int i = 0; i < town.SquareOutlines.Length; i++)
            {
                if (x < town.SquareBounds[i].min.x || x > town.SquareBounds[i].max.x
                    || z < town.SquareBounds[i].min.z || z > town.SquareBounds[i].max.z)
                {
                    continue;
                }

                if (StreetNetwork.Contains(town.SquareOutlines[i], at))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Normalised position up the climb: 0 at the foot of the pass, 1 at the summit.</summary>
        public float ClimbFraction(float elevation)
        {
            float span = Mathf.Max(1f, SummitElevation - LowestElevation);
            return Mathf.Clamp01((elevation - LowestElevation) / span);
        }

        /// <summary>True where a tunnel body stands, or where a viewpoint needs its view.</summary>
        /// <param name="tallOnly">
        /// Viewpoints only block things that get in the way of looking. Grass in front of a viewpoint is
        /// fine; a spruce is not.
        /// </param>
        public bool IsBlocked(float x, float z, bool tallOnly)
        {
            for (int s = 0; s < towns.Length; s++)
            {
                TownKeepOut town = towns[s];

                if (town.HasStreets && x >= town.Bounds.min.x && x <= town.Bounds.max.x
                    && z >= town.Bounds.min.z && z <= town.Bounds.max.z)
                {
                    // Clear of the paved surface for everything, and a little further for anything with
                    // a canopy — a spruce whose trunk is beside the kerb still has its branches over the
                    // carriageway.
                    float margin = tallOnly ? 2.5f : 0.5f;

                    float toStreet = town.Streets.DistanceTo(x, z, out int edgeIndex);
                    if (edgeIndex >= 0 && toStreet < town.Edges[edgeIndex].HalfOuter + margin)
                    {
                        return true;
                    }

                    if (IsOnPaving(town, x, z))
                    {
                        return true;
                    }

                    for (int i = 0; i < town.Junctions.Length; i++)
                    {
                        float dx = town.Junctions[i].x - x;
                        float dz = town.Junctions[i].z - z;
                        float reach = town.JunctionRadius[i] + margin;

                        if (dx * dx + dz * dz < reach * reach)
                        {
                            return true;
                        }
                    }
                }

                if (town.Plan == null)
                {
                    continue;
                }

                // Nothing wild grows through a wall — but only the wall. Testing the whole plot radius
                // here was what left the town on bare earth: 14.9 m per plot at 26 m spacing merges into
                // one continuous dead strip down every street, grass included.
                if (town.Plan.IsBuiltOn(x, z, town.PlotClearance))
                {
                    return true;
                }

                // Tall things keep off the gardens and off the streets, so a spruce cannot come up
                // through someone's washing line. Grass and shrubs carry on right up to the houses,
                // which is most of what makes a town look lived in rather than abandoned.
                if (tallOnly && town.Plan.IsOccupied(x, z, town.TreeKeepOut))
                {
                    return true;
                }
            }

            if (!hasBlockers || x < minX || x > maxX || z < minZ || z > maxZ)
            {
                return false;
            }

            if (WithinAny(blockers, blockerRadius, x, z))
            {
                return true;
            }

            return tallOnly && WithinAny(viewpoints, viewpointRadius, x, z);
        }


        private static bool WithinAny(Vector3[] points, float radius, float x, float z)
        {
            float radiusSqr = radius * radius;

            for (int i = 0; i < points.Length; i++)
            {
                float dx = points[i].x - x;
                float dz = points[i].z - z;
                if (dx * dx + dz * dz <= radiusSqr)
                {
                    return true;
                }
            }

            return false;
        }

        private void Encapsulate(Vector3[] points, float radius)
        {
            for (int i = 0; i < points.Length; i++)
            {
                minX = Mathf.Min(minX, points[i].x - radius);
                maxX = Mathf.Max(maxX, points[i].x + radius);
                minZ = Mathf.Min(minZ, points[i].z - radius);
                maxZ = Mathf.Max(maxZ, points[i].z + radius);
            }
        }
    }

    /// <summary>
    /// Scatters plants over one terrain tile and returns them as a single mesh.
    ///
    /// One merged mesh per tile rather than one object per plant, for the same reason the guard rails are one
    /// mesh: the scene is checked-in YAML, and several thousand tree objects would be unreadable in it and
    /// slow to load. Per tile rather than one mesh for the world — unlike the rails there are hundreds of
    /// thousands of triangles here, so it has to be able to stream, and parenting the mesh under a tile's
    /// existing <see cref="WorldChunk"/> makes that free.
    ///
    /// Placement is by hashed jittered grid, never by a running random sequence. A candidate's fate depends
    /// only on its own cell coordinates, so a rebuild is byte-identical and the order tiles happen to be
    /// generated in cannot change what grows.
    /// </summary>
    public static class VegetationBuilder
    {
        private const int TreeSpecies = 1;
        private const int ShrubSpecies = 2;
        private const int TuftSpecies = 3;
        private const int BoulderSpecies = 4;

        /// <summary>How far a plant leans towards the slope it stands on. Fully aligned looks like it fell over.</summary>
        private const float SlopeLean = 0.3f;

        /// <summary>
        /// Builds every plant on one tile. Returns null when nothing grew, which is normal above the tree
        /// line and on the tiles that are all tunnel.
        /// </summary>
        public static Mesh BuildTile(
            TerrainTileKey key,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            string meshName,
            out VegetationStats stats)
        {
            stats = new VegetationStats();

            var buffer = new VegetationMeshBuffer(PlantMeshes.SubmeshCount);
            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            ScatterTrees(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats);
            ScatterShrubs(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats);
            ScatterTufts(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats);
            ScatterBoulders(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats);

            stats.Triangles = buffer.TriangleCount;
            stats.Flips = buffer.FlipCount;

            // Bark, conifer, broadleaf and undergrowth fold into one submesh carrying their colours.
            // Four flat colours that appear on nearly every tile were four draw calls on nearly every
            // tile — the largest single line in the draw-call budget, larger than the town's.
            buffer.MergeTinted(PlantMeshes.FoliageTints());

            return buffer.ToMesh(meshName, stats.Submeshes);
        }

        private static void ScatterTrees(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats)
        {
            float cell = shape.TreeCellSize;
            float minSlopeCosine = Mathf.Cos(shape.TreeMaxSlopeDegrees * Mathf.Deg2Rad);

            int fromX = Mathf.FloorToInt(originX / cell);
            int toX = Mathf.CeilToInt((originX + tileSize) / cell);
            int fromZ = Mathf.FloorToInt(originZ / cell);
            int toZ = Mathf.CeilToInt((originZ + tileSize) / cell);

            for (int gz = fromZ; gz <= toZ; gz++)
            {
                for (int gx = fromX; gx <= toX; gx++)
                {
                    if (!OwnsCell(gx, gz, cell, originX, originZ, tileSize))
                    {
                        continue;
                    }

                    var random = new PlantRandom(Hash(gx, gz, TreeSpecies));
                    float x = (gx + 0.5f) * cell + random.Range(-0.42f, 0.42f) * cell;
                    float z = (gz + 0.5f) * cell + random.Range(-0.42f, 0.42f) * cell;

                    float toRoad = field.DistanceToRoad(x, z);
                    if (toRoad < shape.TreeClearance || !PassesFalloff(shape, toRoad, ref random))
                    {
                        continue;
                    }

                    if (Clump(x, z, shape.ClumpScale, 0f) < shape.ClumpThreshold)
                    {
                        continue;
                    }

                    if (context.IsBlocked(x, z, true))
                    {
                        continue;
                    }

                    TerrainTileBuilder.SampleSurface(field, terrainShape, x, z,
                        out Vector3 point, out Vector3 normal);

                    if (normal.y < minSlopeCosine)
                    {
                        continue;
                    }

                    float climb = context.ClimbFraction(point.y);
                    float treeLine = shape.TreeLineHeight
                                     + (Clump(x, z, 0.004f, 71.3f) - 0.5f) * 2f * shape.TreeLineJitter;

                    if (climb > treeLine)
                    {
                        // Just above the line, the odd dead trunk left standing. It is the cheapest way to
                        // make the transition from forest to rock read as a transition rather than an edge.
                        if (climb < treeLine + shape.SnagBand && random.Chance(shape.SnagChance))
                        {
                            PlantMeshes.AddSnag(buffer, Place(point, normal, ref random, 1f));
                            stats.Snags++;
                            Record(stats, toRoad, context, x, z);
                        }

                        continue;
                    }

                    // Trees get shorter as they approach the line, which is what actually communicates
                    // altitude — a forest that simply stops looks mown.
                    float scale = Mathf.Lerp(1f, 0.55f,
                        Mathf.InverseLerp(treeLine - 0.25f, treeLine, climb));

                    // Broadleaf in the valley giving way to conifer up the mountain. Never pure either way:
                    // a single species over a whole flank reads as a texture rather than a wood.
                    float coniferBias = Mathf.SmoothStep(0f, 1f,
                        Mathf.InverseLerp(shape.BroadleafBelow - 0.12f, shape.BroadleafBelow + 0.22f, climb));

                    PlantPlacement placement = Place(point, normal, ref random, scale);

                    // Even the valley floor keeps a good share of spruce — this is a mountain, and a pure
                    // broadleaf band at the bottom would read as a different country from the top.
                    if (random.Next() < Mathf.Lerp(0.45f, 1f, coniferBias))
                    {
                        PlantMeshes.AddConifer(buffer, placement);
                        stats.Conifers++;
                    }
                    else
                    {
                        PlantMeshes.AddBroadleaf(buffer, placement);
                        stats.Broadleaves++;
                    }

                    Record(stats, toRoad, context, x, z);
                }
            }
        }

        private static void ScatterShrubs(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats)
        {
            float cell = shape.ShrubCellSize;
            float minSlopeCosine = Mathf.Cos(shape.ShrubMaxSlopeDegrees * Mathf.Deg2Rad);

            int fromX = Mathf.FloorToInt(originX / cell);
            int toX = Mathf.CeilToInt((originX + tileSize) / cell);
            int fromZ = Mathf.FloorToInt(originZ / cell);
            int toZ = Mathf.CeilToInt((originZ + tileSize) / cell);

            for (int gz = fromZ; gz <= toZ; gz++)
            {
                for (int gx = fromX; gx <= toX; gx++)
                {
                    if (!OwnsCell(gx, gz, cell, originX, originZ, tileSize))
                    {
                        continue;
                    }

                    var random = new PlantRandom(Hash(gx, gz, ShrubSpecies));
                    float x = (gx + 0.5f) * cell + random.Range(-0.45f, 0.45f) * cell;
                    float z = (gz + 0.5f) * cell + random.Range(-0.45f, 0.45f) * cell;

                    float toRoad = field.DistanceToRoad(x, z);
                    if (toRoad < shape.ShrubClearance || !PassesFalloff(shape, toRoad, ref random))
                    {
                        continue;
                    }

                    // Offset noise, so bushes are not simply the same stands as the trees. They thin out
                    // rather than stopping, which is what fills a clearing instead of leaving bare grass.
                    if (Clump(x, z, shape.ClumpScale * 1.6f, 17.9f) < shape.ClumpThreshold * 0.7f)
                    {
                        continue;
                    }

                    if (context.IsBlocked(x, z, false))
                    {
                        continue;
                    }

                    TerrainTileBuilder.SampleSurface(field, terrainShape, x, z,
                        out Vector3 point, out Vector3 normal);

                    if (normal.y < minSlopeCosine)
                    {
                        continue;
                    }

                    // Dwarf scrub carries on past the tree line, thinning towards the summit but never
                    // stopping dead. Cutting it off at a line left the top of the pass as plain green
                    // ground with a few rocks on it, which read as unfinished rather than as high.
                    float climb = context.ClimbFraction(point.y);
                    if (climb > shape.TreeLineHeight)
                    {
                        float above = Mathf.InverseLerp(shape.TreeLineHeight, 1f, climb);
                        if (!random.Chance(Mathf.Lerp(0.85f, 0.25f, above)))
                        {
                            continue;
                        }
                    }

                    float scale = Mathf.Lerp(1f, 0.5f,
                        Mathf.InverseLerp(shape.TreeLineHeight - 0.1f, 1f, climb));

                    PlantMeshes.AddShrub(buffer, Place(point, normal, ref random, scale));
                    stats.Shrubs++;
                    Record(stats, toRoad, context, x, z);
                }
            }
        }

        private static void ScatterTufts(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats)
        {
            float cell = shape.TuftCellSize;
            float minSlopeCosine = Mathf.Cos(shape.TuftMaxSlopeDegrees * Mathf.Deg2Rad);

            int fromX = Mathf.FloorToInt(originX / cell);
            int toX = Mathf.CeilToInt((originX + tileSize) / cell);
            int fromZ = Mathf.FloorToInt(originZ / cell);
            int toZ = Mathf.CeilToInt((originZ + tileSize) / cell);

            for (int gz = fromZ; gz <= toZ; gz++)
            {
                for (int gx = fromX; gx <= toX; gx++)
                {
                    if (!OwnsCell(gx, gz, cell, originX, originZ, tileSize))
                    {
                        continue;
                    }

                    var random = new PlantRandom(Hash(gx, gz, TuftSpecies));
                    float x = (gx + 0.5f) * cell + random.Range(-0.48f, 0.48f) * cell;
                    float z = (gz + 0.5f) * cell + random.Range(-0.48f, 0.48f) * cell;

                    // Grass exists for the band the driver stares at all the way up the pass, and nowhere
                    // else — it is by far the most numerous plant and by far the least visible at distance.
                    float toRoad = field.DistanceToRoad(x, z);
                    if (toRoad < shape.TuftClearance || toRoad > shape.TuftMaxDistance)
                    {
                        continue;
                    }

                    if (Clump(x, z, shape.ClumpScale * 3f, 43.1f) < 0.35f)
                    {
                        continue;
                    }

                    if (context.IsBlocked(x, z, false))
                    {
                        continue;
                    }

                    TerrainTileBuilder.SampleSurface(field, terrainShape, x, z,
                        out Vector3 point, out Vector3 normal);

                    if (normal.y < minSlopeCosine)
                    {
                        continue;
                    }

                    // No altitude limit: alpine grass is exactly what covers the ground above the tree line,
                    // and at six triangles a tuft it is the cheapest thing in the whole system.
                    PlantMeshes.AddGrassTuft(buffer, Place(point, normal, ref random, random.Range(0.8f, 1.3f)));
                    stats.Tufts++;
                    Record(stats, toRoad, context, x, z);
                }
            }
        }

        private static void ScatterBoulders(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats)
        {
            float cell = shape.BoulderCellSize;
            float steepCosine = Mathf.Cos(shape.BoulderMinSlopeDegrees * Mathf.Deg2Rad);

            int fromX = Mathf.FloorToInt(originX / cell);
            int toX = Mathf.CeilToInt((originX + tileSize) / cell);
            int fromZ = Mathf.FloorToInt(originZ / cell);
            int toZ = Mathf.CeilToInt((originZ + tileSize) / cell);

            for (int gz = fromZ; gz <= toZ; gz++)
            {
                for (int gx = fromX; gx <= toX; gx++)
                {
                    if (!OwnsCell(gx, gz, cell, originX, originZ, tileSize))
                    {
                        continue;
                    }

                    var random = new PlantRandom(Hash(gx, gz, BoulderSpecies));
                    float x = (gx + 0.5f) * cell + random.Range(-0.45f, 0.45f) * cell;
                    float z = (gz + 0.5f) * cell + random.Range(-0.45f, 0.45f) * cell;

                    float toRoad = field.DistanceToRoad(x, z);
                    if (toRoad < shape.BoulderClearance)
                    {
                        continue;
                    }

                    // Boulders ask as a tall thing. The tooltip on TreeKeepOut always claimed they did
                    // and they did not — an erratic between two front gardens looks like a mistake.
                    if (context.IsBlocked(x, z, true))
                    {
                        continue;
                    }

                    TerrainTileBuilder.SampleSurface(field, terrainShape, x, z,
                        out Vector3 point, out Vector3 normal);

                    // Boulders are the answer to the ground vegetation refuses: screes and the bare summit.
                    // Everywhere else they are occasional, so a meadow still gets the odd erratic.
                    bool steep = normal.y < steepCosine;
                    bool high = context.ClimbFraction(point.y) > shape.TreeLineHeight;
                    float chance = steep || high ? 0.7f : 0.1f;

                    if (!random.Chance(chance))
                    {
                        continue;
                    }

                    PlantMeshes.AddBoulder(buffer, Place(point, normal, ref random, 1f));
                    stats.Boulders++;
                    Record(stats, toRoad, context, x, z);
                }
            }
        }

        /// <summary>
        /// Whether this tile owns the candidate cell, decided by where the cell centre falls.
        ///
        /// The candidate grids do not divide the tile size — 11 m trees on a 168 m tile — so cells straddle
        /// tile edges. Ownership by centre gives every cell exactly one owner, which is what stops a seam
        /// growing two trees or none. The jittered position may then land slightly outside the tile, and that
        /// is fine: the mesh bounds cover it and the chunk radius is recalculated afterwards.
        /// </summary>
        private static bool OwnsCell(int gx, int gz, float cell, float originX, float originZ, float tileSize)
        {
            float centreX = (gx + 0.5f) * cell;
            float centreZ = (gz + 0.5f) * cell;

            return centreX >= originX && centreX < originX + tileSize
                   && centreZ >= originZ && centreZ < originZ + tileSize;
        }

        /// <summary>
        /// Thins the far half of the corridor. It is seen edge-on through fog, so full density there costs
        /// as much as the near field and shows almost nothing.
        /// </summary>
        private static bool PassesFalloff(in VegetationShape shape, float distanceToRoad, ref PlantRandom random)
        {
            if (distanceToRoad <= shape.FarDensityStart)
            {
                return true;
            }

            float t = Mathf.InverseLerp(shape.FarDensityStart, shape.FarDensityEnd, distanceToRoad);
            return random.Chance(Mathf.Lerp(1f, shape.FarDensity, t));
        }

        /// <summary>
        /// Clumping mask. Perlin is deterministic in Unity, so this survives a rebuild unchanged.
        ///
        /// The constant is not cosmetic: the course starts at z = -260 and Unity's Perlin noise mirrors
        /// about the origin for negative inputs. Without the shift there would be a seam of suspiciously
        /// symmetrical forest running along z = 0.
        /// </summary>
        private static float Clump(float x, float z, float scale, float offset)
        {
            const float positiveShift = 512f;
            return Mathf.PerlinNoise(
                x * scale + offset + positiveShift,
                z * scale + offset + positiveShift);
        }

        private static PlantPlacement Place(
            Vector3 point,
            Vector3 normal,
            ref PlantRandom random,
            float scale)
        {
            // Partly, not fully, aligned to the slope: a tree standing exactly normal to a hillside looks
            // like it is falling over, and one standing exactly vertical looks stamped on.
            Vector3 up = Vector3.Lerp(Vector3.up, normal, SlopeLean).normalized;
            return new PlantPlacement(point, up, random.Range(0f, Mathf.PI * 2f), scale, random.NextSeed());
        }

        private static void Record(
            VegetationStats stats, float distanceToRoad, VegetationContext context, float x, float z)
        {
            if (distanceToRoad < stats.ClosestToRoad)
            {
                stats.ClosestToRoad = distanceToRoad;
            }

            float margin = context.PavedMargin(x, z);
            if (margin < stats.ClosestToStreet)
            {
                stats.ClosestToStreet = margin;
            }
        }

        /// <summary>
        /// FNV-1a over the cell coordinates plus a final avalanche. A hash rather than a sequence, so a
        /// candidate depends on nothing but where it is.
        /// </summary>
        private static uint Hash(int gx, int gz, int species)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)gx) * 16777619u;
                hash = (hash ^ (uint)gz) * 16777619u;
                hash = (hash ^ (uint)species) * 16777619u;

                hash ^= hash >> 15;
                hash *= 2246822519u;
                hash ^= hash >> 13;
                hash *= 3266489917u;
                hash ^= hash >> 16;
                return hash;
            }
        }
    }
}
