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

        /// <summary>The Ebental's own, counted apart so the log can say whether the avenue actually stood up.</summary>
        public int Poplars;

        /// <summary>
        /// Anadolu's spires. The same mesh as <see cref="Poplars"/> and counted apart from them anyway,
        /// because they are the only two things in the world that share a silhouette and are meant to
        /// read as different species — so one number covering both would hide either of them failing.
        /// </summary>
        public int Cypresses;

        public int FruitTrees;
        public int HayBales;
        public int WallRuns;
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

        public int Plants => Conifers + Broadleaves + Shrubs + Tufts + Boulders + Snags
                             + Poplars + Cypresses + FruitTrees;

        public void Add(VegetationStats other)
        {
            Conifers += other.Conifers;
            Broadleaves += other.Broadleaves;
            Shrubs += other.Shrubs;
            Tufts += other.Tufts;
            Boulders += other.Boulders;
            Snags += other.Snags;
            Poplars += other.Poplars;

            // Every field on this class has to be listed here, and forgetting one is silent: the tile
            // builds what it was going to build and the total simply reports nought. Cypresses came in
            // and were missed, and the log said "0 cypresses" over eleven hundred of them — which read
            // as a region that had stopped planting rather than as a line missing from a sum.
            Cypresses += other.Cypresses;
            FruitTrees += other.FruitTrees;
            HayBales += other.HayBales;
            WallRuns += other.WallRuns;
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

        /// <summary>
        /// Where the filling stations stand, in plan.
        ///
        /// <para>Its own list rather than a seventh entry in <see cref="viewpoints"/>, because the two
        /// keep out different things. A viewpoint only stops what gets in the way of looking, so grass
        /// and bushes are welcome in front of it and only trees are turned away — that is what the
        /// <c>tallOnly</c> gate on it means. A forecourt is a concrete slab, and nothing grows out of
        /// concrete.</para>
        /// </summary>
        private readonly Vector3[] pads;

        /// <summary>
        /// Where the avenue trees stand, in plan.
        ///
        /// <para>Worked out once here rather than per tile, exactly as the blockers and the viewpoints
        /// are. A tile that derived them itself would walk the whole five kilometres of road to find the
        /// twenty stations inside it, six hundred and thirty-three times over.</para>
        /// </summary>
        private readonly Vector2[] avenue;
        private readonly float blockerRadius;
        private readonly float viewpointRadius;
        private readonly float padRadius;

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

        /// <param name="path">
        /// The road the climb axis is taken from, and the first road whose features are read.
        /// </param>
        /// <param name="course">Its course. Null leaves the world with no tunnels and no viewpoints.</param>
        /// <param name="others">
        /// Any other road with features worth clearing around.
        ///
        /// <para>Features only — <b>not</b> the climb axis, and that asymmetry is deliberate. The tree
        /// line is a fraction between <see cref="LowestElevation"/> and <see cref="SummitElevation"/>,
        /// and those come from the mountain the tree line belongs to. Widening the span to take in the
        /// motorway at −25 m or the coast road at −46 would move the tree line on the pass, several
        /// kilometres from either.</para>
        ///
        /// <para>What a second road does need is its viewpoints kept clear. A viewpoint with a forest
        /// grown over it is a lay-by, and nothing else in the build would report it.</para>
        /// </param>
        public VegetationContext(
            IRoadPath path,
            RoadCourse course,
            in VegetationShape shape,
            IReadOnlyList<TownSource> settlements = null,
            IReadOnlyList<MountainField.FieldRoad> others = null,
            IRoadPath avenueRoad = null,
            IReadOnlyList<Vector3> forecourts = null)
        {
            blockerRadius = shape.TunnelExclusion;
            viewpointRadius = shape.ViewpointClearing;
            padRadius = shape.FuelStationClearing;

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

            AddFeatures(path, course, shape, covered, views);

            for (int i = 0; others != null && i < others.Count; i++)
            {
                AddFeatures(others[i].Path, others[i].Course, shape, covered, views);
            }

            blockers = covered.ToArray();
            viewpoints = views.ToArray();

            // Handed in rather than read off the courses, and it has to be: a forecourt's centre is a
            // carriageway half-width, a verge gap and an apron half-depth out from the road, and the
            // first of those is different on the motorway from on the pass. Deriving it here would mean
            // a second copy of FuelStationBuilder's arithmetic that agreed with the first until one of
            // them was edited.
            //
            // Getting it wrong is not subtle and was not caught by any check: keyed on the road point
            // instead, a 30 m radius reached 30 m from the centreline while the apron reached 43, and
            // bushes came up through the far third of the concrete. It took a photograph to see.
            pads = forecourts != null ? new List<Vector3>(forecourts).ToArray() : new Vector3[0];
            avenue = AvenueStations(avenueRoad);

            LowestElevation = course != null ? course.LowestElevation : 0f;
            SummitElevation = course != null ? course.Summit.y : LowestElevation + 1f;

            hasBlockers = blockers.Length > 0 || viewpoints.Length > 0 || pads.Length > 0;
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
            Encapsulate(pads, padRadius);
        }

        /// <summary>
        /// Reads one road's features into the three keep-out lists: a clearing at every viewpoint, a
        /// paved area at every filling station, and a run of blockers along everything roofed.
        ///
        /// <para><b>The last branch is a catch-all, and that is the thing to know before adding a
        /// feature kind.</b> Anything not named above it is treated as a tunnel body — a capsule of the
        /// full <c>TunnelExclusion</c> width running <c>TunnelEndMargin</c> past both ends, blocking
        /// every species including grass. A new kind that forgets to declare itself here does not fail;
        /// it quietly carves a hole in the world.</para>
        /// </summary>
        private static void AddFeatures(
            IRoadPath path,
            RoadCourse course,
            in VegetationShape shape,
            List<Vector3> covered,
            List<Vector3> views)
        {
            if (path != null && course != null)
            {
                for (int i = 0; i < course.Features.Count; i++)
                {
                    RoadFeature feature = course.Features[i];

                    if (feature.Kind == RoadFeatureKind.Viewpoint)
                    {
                        views.Add(path.GetPositionAtDistance(
                            Mathf.Clamp(feature.StartDistance, 0f, path.Length)));
                        continue;
                    }

                    // A forecourt is not a tunnel either, and for the sharper of the two reasons: the
                    // capsule below would run its full end margin past both ends of a feature that has
                    // no length at all, so a station would block nearly twice the ground it stands on.
                    //
                    // Nothing is recorded here. Where the forecourt actually is comes in through the
                    // constructor, because only the caller knows how wide the road under it is — see the
                    // note on `pads`. This branch exists purely so the catch-all below never sees one.
                    if (feature.Kind == RoadFeatureKind.FuelStation)
                    {
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
        }

        public float LowestElevation { get; }

        public float SummitElevation { get; }

        /// <summary>The avenue's stations. Empty where no road was handed in.</summary>
        public IReadOnlyList<Vector2> Avenue => avenue;

        /// <summary>Spacing of the avenue along the road, metres.</summary>
        private const float AvenueSpacing = 18f;

        /// <summary>
        /// How far out from the centreline the trunks stand, metres.
        ///
        /// <para>9.5, against a paved half-width of 6.75 and a delineator line just outside it. Closer
        /// and the canopies lean over the carriageway; much further and the two rows stop reading as one
        /// avenue and become a wood with a gap in it.</para>
        /// </summary>
        private const float AvenueOffset = 9.5f;

        /// <summary>
        /// Below this radius the inside of a bend gets no trees, metres.
        ///
        /// <para>Sight line, not clearance. On the inside of a tight bend a row of 20 m poplars is a wall
        /// across the exit of the corner — the driver would be reading the outer row through the gaps in
        /// the inner one. Real avenues thin out on the inside of bends for the same reason.</para>
        /// </summary>
        private const float AvenueOpenRadius = 260f;

        /// <summary>
        /// Walks the road once and lists where the avenue trees go.
        ///
        /// <para>Skips the inside of tight bends, and does no ground, water or keep-out testing at all —
        /// those are questions about a place rather than about the road, and the tile that owns the
        /// station is the one holding the height field when it draws it.</para>
        /// </summary>
        private static Vector2[] AvenueStations(IRoadPath road)
        {
            if (road == null || road.Length < AvenueSpacing)
            {
                return System.Array.Empty<Vector2>();
            }

            var stations = new List<Vector2>(1024);

            for (float at = AvenueSpacing; at < road.Length - AvenueSpacing; at += AvenueSpacing)
            {
                Vector3 centre = road.GetPositionAtDistance(at);
                Vector3 right = road.GetRightAtDistance(at);

                float radius = road.GetRadiusAtDistance(at, 10f);
                float curvature = road.GetSignedCurvatureAtDistance(at, 10f);

                // Positive curvature turns towards the right, so the inside of the bend is the right.
                bool tight = radius < AvenueOpenRadius;
                bool skipRight = tight && curvature > 0f;
                bool skipLeft = tight && curvature < 0f;

                if (!skipRight)
                {
                    Vector3 on = centre + right * AvenueOffset;
                    stations.Add(new Vector2(on.x, on.z));
                }

                if (!skipLeft)
                {
                    Vector3 on = centre - right * AvenueOffset;
                    stations.Add(new Vector2(on.x, on.z));
                }
            }

            return stations.ToArray();
        }

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

            // Unconditional, not under tallOnly: see the note on the field. Tested before the
            // viewpoints because it is the cheaper of the two — six points against as many as ten —
            // and because it rejects outright rather than only for trees.
            if (WithinAny(pads, padRadius, x, z))
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

        // Their own numbers, and they have to be. Hash(gx, gz, species) is what separates one scatter's
        // candidate grid from another's; reuse a number and the orchard comes up planted exactly where
        // the spruces already are.
        private const int OrchardSpecies = 5;
        private const int BaleSpecies = 6;
        private const int PoplarSpecies = 7;

        /// <summary>
        /// How far above a water surface a plant still counts as standing in it, metres.
        ///
        /// <para>Half a metre, so the bank is planted right up to the shore and nothing stands with its
        /// feet in the shallows. Zero would leave a fringe of trees exactly on the waterline, which is
        /// the one place they read as an error.</para>
        /// </summary>
        private const float WaterFreeboard = 0.5f;

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
            out VegetationStats stats,
            LandRegion region = null)
        {
            stats = new VegetationStats();

            var buffer = new VegetationMeshBuffer(PlantMeshes.SubmeshCount);
            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            // Asked once for the tile rather than per candidate: the region's weight is a smooth field
            // and cannot appear inside 168 m, and the query walks a bucket grid.
            LandRegion tileRegion =
                region != null && region.Reaches(originX + tileSize * 0.5f, originZ + tileSize * 0.5f,
                    tileSize * 0.5f * Mathf.Sqrt(2f))
                    ? region
                    : null;

            ScatterTrees(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats,
                tileRegion);
            ScatterShrubs(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats);
            ScatterTufts(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats);
            ScatterBoulders(buffer, field, terrainShape, shape, context, originX, originZ, tileSize, stats,
                tileRegion);

            // Everything below this line is the furniture of worked land — planted rows, cut hay, walled
            // boundaries — and it used to run for any region there was, because the only region there
            // was happened to be farmland. See LandRegion.Farmed for what that put on the far shore.
            if (tileRegion != null && tileRegion.Farmed)
            {
                ScatterOrchard(buffer, field, terrainShape, shape, context, originX, originZ, tileSize,
                    stats, tileRegion);
                ScatterBales(buffer, field, terrainShape, shape, context, originX, originZ, tileSize,
                    stats, tileRegion);
                BuildFieldBoundaries(buffer, field, terrainShape, context, originX, originZ, tileSize,
                    stats, tileRegion);
                PlantAvenue(buffer, field, terrainShape, context, originX, originZ, tileSize, stats);
            }

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
            VegetationStats stats,
            LandRegion region)
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

                    // Nothing grows in a river, and nothing tall grows on its bank either. The first
                    // test is the basin — carved into the ground this scatter reads, so without it every
                    // body of water comes up wooded. The second is the bank, and it is a separate
                    // question: a tree standing beside the waterline is not in the water and passes the
                    // first test perfectly, which is how every shore in the world ended up with a wall
                    // of canopy on it. See VegetationShape.ShoreTreeClearing.
                    if (field.IsUnderWater(x, z, point.y, WaterFreeboard)
                        || field.IsShore(x, z, point.y, WaterFreeboard, shape.ShoreTreeClearing))
                    {
                        continue;
                    }

                    if (normal.y < minSlopeCosine)
                    {
                        continue;
                    }

                    // In a farmed region most of the wood was cleared long ago, and what is left is
                    // hedgerow and copse. Thinning here rather than with a bigger cell size keeps the
                    // clumping — so the survivors stand in stands, the way trees left on farmland do,
                    // instead of being spread evenly at low density like an orchard nobody planted.
                    float regionWeight = region != null ? region.Weight(x, z) : 0f;

                    if (regionWeight > 0f
                        && !random.Chance(Mathf.Lerp(1f, region.WildTreeChance, regionWeight)))
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

                    // The region overrules the mountain's own mix, and this is the single change that
                    // does most of the work. ClimbFraction is normalised against the pass, so down here
                    // it never leaves 0..0.2, coniferBias is nought, and the line below pins the spruce
                    // probability at its floor of 0.45 — half the trees in the orchard country came out
                    // alpine. Below, the wood is broadleaf and it is autumn.
                    if (regionWeight > 0.5f)
                    {
                        // A spire rather than a crown, where the region asks for them. The mesh is the
                        // avenue's poplar: a poplar and a cypress are the same silhouette at any
                        // distance either is seen from, and what makes these read as somewhere else is
                        // that they are scattered instead of planted in a row. See
                        // LandRegion.SpireChance.
                        if (region.SpireChance > 0f && random.Next() < region.SpireChance)
                        {
                            // The cypress slot, not the poplar's. They are the same mesh on purpose —
                            // see AddPoplar — but the poplar's slot is painted the Ebental's autumn
                            // gold, so sharing it put half the far shore's trees in the colour of the
                            // country road five kilometres back. See PlantMeshes.CypressSubmesh.
                            PlantMeshes.AddPoplar(buffer, placement, PlantMeshes.CypressSubmesh);
                            stats.Cypresses++;
                            Record(stats, toRoad, context, x, z);
                            continue;
                        }

                        if (region.AutumnCanopy)
                        {
                            PlantMeshes.AddBroadleaf(buffer, placement, PlantMeshes.AutumnCanopySubmesh);
                            stats.Broadleaves++;
                            Record(stats, toRoad, context, x, z);
                            continue;
                        }
                    }

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

        /// <summary>
        /// The avenue: a poplar every eighteen metres either side of the country road.
        ///
        /// <para><b>This is the region's signature, and it is a road-follower rather than a scatter for
        /// exactly that reason.</b> Scattered trees at any density read as country; two rows at even
        /// spacing read as a road somebody planted, and they stay readable at a distance where the trees
        /// themselves are four pixels tall. Nothing else in this world tells the driver where they are
        /// from a kilometre away.</para>
        ///
        /// <para>Emitted per tile out of the context's precomputed list, so the avenue streams with the
        /// ground under it instead of becoming one five-kilometre mesh that can never unload.</para>
        /// </summary>
        private static void PlantAvenue(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats)
        {
            IReadOnlyList<Vector2> stations = context.Avenue;

            for (int i = 0; i < stations.Count; i++)
            {
                Vector2 at = stations[i];

                if (at.x < originX || at.x >= originX + tileSize
                    || at.y < originZ || at.y >= originZ + tileSize)
                {
                    continue;
                }

                // A viewpoint with an avenue across it is a lay-by. IsBlocked answers for the town
                // keep-outs and the tunnels in the same call.
                if (context.IsBlocked(at.x, at.y, true))
                {
                    continue;
                }

                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.y,
                    out Vector3 point, out Vector3 normal);

                if (field.IsUnderWater(at.x, at.y, point.y, WaterFreeboard))
                {
                    continue;
                }

                var random = new PlantRandom(Hash(i, 0, PoplarSpecies));

                // Upright, not leaning with the slope. A poplar is the one tree here whose whole job is
                // to be a vertical line, and a row of them each tipped a few degrees with the ground is
                // a row of them that no longer rhymes.
                var placement = new PlantPlacement(
                    point, Vector3.up, random.Range(0f, Mathf.PI * 2f), random.Range(0.9f, 1.08f),
                    random.NextSeed());

                PlantMeshes.AddPoplar(buffer, placement);
                stats.Poplars++;
                Record(stats, field.DistanceToRoad(at.x, at.y), context, at.x, at.y);
            }
        }

        /// <summary>
        /// Orchard rows: fruit trees on a grid laid in the fields' own frame, on about one field in five.
        ///
        /// <para>Rows rather than a scatter, and on the field grid rather than a grid of their own. A
        /// planted row is the difference between land somebody works and land nobody has cleared, and it
        /// costs nothing extra because the boundaries and the ground colour are already using this
        /// frame.</para>
        /// </summary>
        private static void ScatterOrchard(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats,
            LandRegion region)
        {
            const float alongRow = 6f;
            const float betweenRows = 7f;

            FieldRange(region, originX, originZ, tileSize,
                out float minU, out float maxU, out float minV, out float maxV);

            int fromU = Mathf.FloorToInt(minU / betweenRows);
            int toU = Mathf.CeilToInt(maxU / betweenRows);
            int fromV = Mathf.FloorToInt(minV / alongRow);
            int toV = Mathf.CeilToInt(maxV / alongRow);

            float minSlopeCosine = Mathf.Cos(shape.TreeMaxSlopeDegrees * Mathf.Deg2Rad);

            for (int gv = fromV; gv <= toV; gv++)
            {
                for (int gu = fromU; gu <= toU; gu++)
                {
                    var random = new PlantRandom(Hash(gu, gv, OrchardSpecies));

                    // Barely jittered. An orchard whose trees wander is a wood, and the whole point of
                    // this pass is the row.
                    Vector2 plan = region.FromField(
                        (gu + 0.5f) * betweenRows + random.Range(-0.12f, 0.12f) * betweenRows,
                        (gv + 0.5f) * alongRow + random.Range(-0.12f, 0.12f) * alongRow);

                    float x = plan.x;
                    float z = plan.y;

                    if (!Owns(x, z, originX, originZ, tileSize))
                    {
                        continue;
                    }

                    if (region.Weight(x, z) < 0.55f)
                    {
                        continue;
                    }

                    // One field in five, and never on ploughed or stubbled ground — an orchard standing
                    // in a furrowed field is two land uses on one parcel.
                    if (region.ParcelValue(x, z, 23u) > 0.2f || region.Parcel(x, z) >= 2)
                    {
                        continue;
                    }

                    float toRoad = field.DistanceToRoad(x, z);
                    if (toRoad < 25f)
                    {
                        continue;
                    }

                    if (context.IsBlocked(x, z, true))
                    {
                        continue;
                    }

                    TerrainTileBuilder.SampleSurface(field, terrainShape, x, z,
                        out Vector3 point, out Vector3 normal);

                    if (field.IsUnderWater(x, z, point.y, WaterFreeboard) || normal.y < minSlopeCosine)
                    {
                        continue;
                    }

                    PlantMeshes.AddFruitTree(buffer, Place(point, normal, ref random, random.Range(0.9f, 1.1f)));
                    stats.FruitTrees++;
                    Record(stats, toRoad, context, x, z);
                }
            }
        }

        /// <summary>Round bales, thinly, and only on the fields that have been cut.</summary>
        private static void ScatterBales(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            in VegetationShape shape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats,
            LandRegion region)
        {
            const float cell = 26f;

            int fromX = Mathf.FloorToInt(originX / cell);
            int toX = Mathf.CeilToInt((originX + tileSize) / cell);
            int fromZ = Mathf.FloorToInt(originZ / cell);
            int toZ = Mathf.CeilToInt((originZ + tileSize) / cell);

            float minSlopeCosine = Mathf.Cos(shape.TuftMaxSlopeDegrees * Mathf.Deg2Rad);

            for (int gz = fromZ; gz <= toZ; gz++)
            {
                for (int gx = fromX; gx <= toX; gx++)
                {
                    if (!OwnsCell(gx, gz, cell, originX, originZ, tileSize))
                    {
                        continue;
                    }

                    var random = new PlantRandom(Hash(gx, gz, BaleSpecies));
                    float x = (gx + 0.5f) * cell + random.Range(-0.45f, 0.45f) * cell;
                    float z = (gz + 0.5f) * cell + random.Range(-0.45f, 0.45f) * cell;

                    // Stubble only: a bale is what is left after a field is cut, so it says which field
                    // was cut. On pasture it would just be a lump.
                    if (region.Weight(x, z) < 0.55f || region.Parcel(x, z) != 2)
                    {
                        continue;
                    }

                    if (!random.Chance(0.45f) || field.DistanceToRoad(x, z) < 18f)
                    {
                        continue;
                    }

                    if (context.IsBlocked(x, z, false))
                    {
                        continue;
                    }

                    TerrainTileBuilder.SampleSurface(field, terrainShape, x, z,
                        out Vector3 point, out Vector3 normal);

                    if (field.IsUnderWater(x, z, point.y, WaterFreeboard) || normal.y < minSlopeCosine)
                    {
                        continue;
                    }

                    EbentalMeshes.AddHayBale(buffer, Place(point, normal, ref random, 1f));
                    stats.HayBales++;
                }
            }
        }

        /// <summary>
        /// Walls and fences on the field boundaries.
        ///
        /// <para><b>The colours alone are not enough and this is why.</b> A patchwork of tints is only
        /// visible while the ground faces the camera; the moment it tilts away, shading swamps it and the
        /// fields dissolve. A line of stone on the boundary is an edge, and an edge survives any angle —
        /// which is what actually makes the valley read as farmed from the driver's seat rather than only
        /// from overhead.</para>
        ///
        /// <para>Each tile draws only the part of a boundary inside itself, so runs abut across tile
        /// seams and nothing is built twice.</para>
        /// </summary>
        private static void BuildFieldBoundaries(
            VegetationMeshBuffer buffer,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationContext context,
            float originX,
            float originZ,
            float tileSize,
            VegetationStats stats,
            LandRegion region)
        {
            FieldRange(region, originX, originZ, tileSize,
                out float minU, out float maxU, out float minV, out float maxV);

            // The two families of boundary line: constant u, and constant v.
            for (int axis = 0; axis < 2; axis++)
            {
                float pitch = axis == 0 ? region.PitchAcross : region.PitchAlong;
                float from = axis == 0 ? minU : minV;
                float to = axis == 0 ? maxU : maxV;

                float spanFrom = axis == 0 ? minV : minU;
                float spanTo = axis == 0 ? maxV : maxU;

                for (int line = Mathf.FloorToInt(from / pitch); line <= Mathf.CeilToInt(to / pitch); line++)
                {
                    float at = line * pitch;

                    // Not every boundary is walled. A field with a hedge line on every side of it is a
                    // maze; about half of them keeps the grid legible without fencing the whole valley.
                    float kind = region.CellValue(line, axis, 41u);
                    if (kind > 0.62f)
                    {
                        continue;
                    }

                    var run = new List<Vector3>(24);

                    const float step = 5f;
                    for (float along = Mathf.Floor(spanFrom / step) * step; along <= spanTo; along += step)
                    {
                        Vector2 plan = axis == 0
                            ? region.FromField(at, along)
                            : region.FromField(along, at);

                        bool inside = plan.x >= originX && plan.x < originX + tileSize
                                      && plan.y >= originZ && plan.y < originZ + tileSize;

                        bool usable = inside
                                      && region.Weight(plan.x, plan.y) > 0.55f
                                      && field.DistanceToRoad(plan.x, plan.y) > 16f
                                      && !context.IsBlocked(plan.x, plan.y, false);

                        if (usable)
                        {
                            TerrainTileBuilder.SampleSurface(field, terrainShape, plan.x, plan.y,
                                out Vector3 point, out Vector3 normal);

                            usable = !field.IsUnderWater(plan.x, plan.y, point.y, WaterFreeboard)
                                     && normal.y > 0.72f;

                            if (usable)
                            {
                                run.Add(point);
                                continue;
                            }
                        }

                        Flush(buffer, run, kind, stats);
                    }

                    Flush(buffer, run, kind, stats);
                }
            }
        }

        /// <summary>Emits an accumulated boundary run and clears it, so a break in the ground breaks the wall.</summary>
        private static void Flush(
            VegetationMeshBuffer buffer, List<Vector3> run, float kind, VegetationStats stats)
        {
            if (run.Count >= 3)
            {
                uint seed = (uint)(run[0].x * 13.7f + run[0].z * 7.1f + 1u);

                if (kind < 0.42f)
                {
                    EbentalMeshes.AddDryStoneWall(buffer, run.ToArray(), seed);
                }
                else
                {
                    EbentalMeshes.AddPostAndRail(buffer, run.ToArray(), seed);
                }

                stats.WallRuns++;
            }

            run.Clear();
        }

        /// <summary>The tile's extent in the region's field frame, from its four corners.</summary>
        private static void FieldRange(
            LandRegion region,
            float originX,
            float originZ,
            float tileSize,
            out float minU,
            out float maxU,
            out float minV,
            out float maxV)
        {
            minU = float.MaxValue;
            maxU = float.MinValue;
            minV = float.MaxValue;
            maxV = float.MinValue;

            for (int corner = 0; corner < 4; corner++)
            {
                float x = originX + ((corner & 1) == 0 ? 0f : tileSize);
                float z = originZ + ((corner & 2) == 0 ? 0f : tileSize);

                region.ToField(x, z, out float u, out float v);

                minU = Mathf.Min(minU, u);
                maxU = Mathf.Max(maxU, u);
                minV = Mathf.Min(minV, v);
                maxV = Mathf.Max(maxV, v);
            }
        }

        /// <summary>Whether a world position falls in this tile. The rotated grids cannot use OwnsCell.</summary>
        private static bool Owns(float x, float z, float originX, float originZ, float tileSize)
        {
            return x >= originX && x < originX + tileSize
                   && z >= originZ && z < originZ + tileSize;
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

                    // Nothing grows in a river. The basin was carved into the ground the scatter reads,
                    // so without this every body of water comes up wooded — see MountainField.IsUnderWater.
                    if (field.IsUnderWater(x, z, point.y, WaterFreeboard))
                    {
                        continue;
                    }

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

                    // Nothing grows in a river. The basin was carved into the ground the scatter reads,
                    // so without this every body of water comes up wooded — see MountainField.IsUnderWater.
                    if (field.IsUnderWater(x, z, point.y, WaterFreeboard))
                    {
                        continue;
                    }

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
            VegetationStats stats,
            LandRegion region)
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

                    // Nothing grows in a river. The basin was carved into the ground the scatter reads,
                    // so without this every body of water comes up wooded — see MountainField.IsUnderWater.
                    if (field.IsUnderWater(x, z, point.y, WaterFreeboard))
                    {
                        continue;
                    }

                    // Boulders are the answer to the ground vegetation refuses: screes and the bare summit.
                    // Everywhere else they are occasional, so a meadow still gets the odd erratic.
                    bool steep = normal.y < steepCosine;
                    bool high = context.ClimbFraction(point.y) > shape.TreeLineHeight;
                    float chance = steep || high ? 0.7f : 0.1f;

                    // Erratics get cleared off farmland — that is what the walls are built out of. Not to
                    // nothing, because the odd one on a bank is exactly the detail that says the field
                    // was won from somewhere.
                    if (region != null)
                    {
                        chance *= Mathf.Lerp(1f, 0.2f, region.Weight(x, z));
                    }

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
