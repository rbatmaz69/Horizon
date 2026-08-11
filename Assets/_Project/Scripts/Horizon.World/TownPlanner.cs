using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// What stands on a plot.
    ///
    /// <para>The numbering is not contiguous and that is deliberate — these are persisted in nothing, but
    /// they are read by name in four files, and renumbering them to tidy the list would be a change with
    /// no upside.</para>
    /// </summary>
    public enum TownPlotKind
    {
        House = 0,

        /// <summary>One of the town's three silhouettes. It was the landmark when this was a village.</summary>
        Windmill = 1,

        /// <summary>The tallest thing in the town, and what it is recognised by from the pass above.</summary>
        Mosque = 5,

        /// <summary>The market square's civic front, on its uphill edge.</summary>
        TownHall = 6,

        /// <summary>At the centre of the market square.</summary>
        Fountain = 7,

        /// <summary>One of the stalls scattered over the market square's paving.</summary>
        Stall = 8,

        Barn = 2,
        Sawmill = 3,
    }

    /// <summary>
    /// One street lamp: where it stands, which way its arm reaches, and the patch of carriageway it
    /// lights.
    ///
    /// <para>The pool is stored as world points rather than as an offset, because its corners have to sit
    /// on the street's own cross-section — a carriageway has a 6 cm crown, and a flat polygon lifted
    /// bodily z-fights against it down the middle of the road. Only the planner has the street, so only
    /// the planner can work them out; the mesh builder just closes the polygon.</para>
    ///
    /// <para><see cref="PoolCount"/> is zero for the lamps along the trunk road. Their pool would have to
    /// lie on the textured asphalt of the pass, and a flat day colour cannot disappear into a texture the
    /// way it disappears into the streets' single-colour surface.</para>
    /// </summary>
    public readonly struct TownLamp
    {
        /// <summary>Corners in a pool, when it has one. A hexagon: round enough at three metres across.</summary>
        public const int PoolCorners = 6;

        public readonly Vector3 Position;
        public readonly float Yaw;
        public readonly uint Seed;

        /// <summary>Index of this lamp's first pool corner in <see cref="TownPlan.LampPools"/>.</summary>
        public readonly int PoolStart;

        public readonly int PoolCount;

        public TownLamp(Vector3 position, float yaw, uint seed, int poolStart, int poolCount)
        {
            Position = position;
            Yaw = yaw;
            Seed = seed;
            PoolStart = poolStart;
            PoolCount = poolCount;
        }
    }

    /// <summary>
    /// Where everything in the town goes, worked out once and then read by several passes: the mesh
    /// builder puts geometry on it, the vegetation scatter keeps out of it, and the setup tool hangs a
    /// collider on each plot.
    ///
    /// One plan rather than each pass deciding for itself, because the three would drift apart and the
    /// result would be a hedge growing through a wall.
    /// </summary>
    public sealed class TownPlan
    {
        public readonly struct Plot
        {
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly float HalfWidth;
            public readonly float HalfDepth;
            public readonly TownPlotKind Kind;
            public readonly bool HasCar;
            public readonly bool Fenced;
            public readonly uint Seed;

            /// <summary>
            /// Fraction of this building's panes that light after dark, from the quarter it stands in.
            ///
            /// Carried on the plot rather than re-derived in the mesh builder for the same reason
            /// everything else here is: the builder runs per terrain tile and has no idea which street a
            /// house faced or what quarter that street was in.
            /// </summary>
            public readonly float LitChance;

            public Plot(Vector3 position, float yaw, float halfWidth, float halfDepth,
                TownPlotKind kind, bool hasCar, bool fenced, float litChance, uint seed)
            {
                Position = position;
                Yaw = yaw;
                HalfWidth = halfWidth;
                HalfDepth = halfDepth;
                Kind = kind;
                HasCar = hasCar;
                Fenced = fenced;
                LitChance = litChance;
                Seed = seed;
            }

            /// <summary>Radius that covers the whole plot — garden and all.</summary>
            public float Radius => Mathf.Sqrt(HalfWidth * HalfWidth + HalfDepth * HalfDepth);

            /// <summary>
            /// Radius of the building alone, not of its garden.
            ///
            /// The distinction is what lets anything grow in the town. Blocking planting over the whole
            /// plot radius meant a 14.9 m circle per plot, and with plots 26 m apart those circles
            /// overlapped into one continuous bare strip down every street. A wall needs 8 m of clearance;
            /// a lawn needs none.
            /// </summary>
            public float BuildingRadius => Kind == TownPlotKind.Mosque ? 16f
                : Kind == TownPlotKind.TownHall ? 15f
                : Kind == TownPlotKind.Windmill ? 11f
                : Kind == TownPlotKind.Barn ? 9f
                : Kind == TownPlotKind.Fountain ? 4f
                : Kind == TownPlotKind.Stall ? 2.5f
                : 7.5f;
        }

        private readonly List<Plot> plots;
        private readonly List<TownLamp> lamps;
        private readonly List<Vector3> lampPools;

        /// <summary>
        /// A uniform grid over the plots, so asking "is anything built here" is a handful of cells
        /// rather than a walk down the whole list.
        ///
        /// <para>This is the one query in the build that is genuinely hot, and it was found by measuring
        /// rather than by suspicion. The vegetation scatter asks it for <i>every plant candidate on every
        /// tile</i> — a few hundred thousand of them — and the list is a hundred and forty plots long, so
        /// the plain version is tens of millions of distance checks and it is most of the forty-five
        /// seconds the terrain phase takes. Counts, prefix offsets, items: the same shape as
        /// <c>MountainField.BuildBuckets</c> and <c>StreetIndex</c>.</para>
        /// </summary>
        private readonly int[] cellStart;

        private readonly int[] cellItems;
        private readonly Vector2 gridOrigin;
        private readonly float cellSize;
        private readonly int columns;
        private readonly int rows;
        private readonly float largestRadius;

        internal TownPlan(List<Plot> plots, List<TownLamp> lamps, List<Vector3> lampPools, Bounds footprint)
        {
            this.plots = plots;
            this.lamps = lamps;
            this.lampPools = lampPools;
            Footprint = footprint;

            cellSize = 32f;

            for (int i = 0; i < plots.Count; i++)
            {
                largestRadius = Mathf.Max(largestRadius, plots[i].Radius);
            }

            gridOrigin = new Vector2(footprint.min.x - cellSize, footprint.min.z - cellSize);
            columns = Mathf.Max(1, Mathf.CeilToInt((footprint.size.x + cellSize * 2f) / cellSize));
            rows = Mathf.Max(1, Mathf.CeilToInt((footprint.size.z + cellSize * 2f) / cellSize));

            int cellCount = columns * rows;
            var counts = new int[cellCount + 1];

            for (int i = 0; i < plots.Count; i++)
            {
                counts[CellOf(plots[i].Position.x, plots[i].Position.z) + 1]++;
            }

            for (int cell = 0; cell < cellCount; cell++)
            {
                counts[cell + 1] += counts[cell];
            }

            cellStart = counts;
            cellItems = new int[plots.Count];
            var cursor = new int[cellCount];

            for (int i = 0; i < plots.Count; i++)
            {
                int cell = CellOf(plots[i].Position.x, plots[i].Position.z);
                cellItems[cellStart[cell] + cursor[cell]] = i;
                cursor[cell]++;
            }
        }

        private int CellOf(float x, float z)
        {
            int column = Mathf.Clamp(Mathf.FloorToInt((x - gridOrigin.x) / cellSize), 0, columns - 1);
            int row = Mathf.Clamp(Mathf.FloorToInt((z - gridOrigin.y) / cellSize), 0, rows - 1);
            return row * columns + column;
        }

        public IReadOnlyList<Plot> Plots => plots;

        public IReadOnlyList<TownLamp> Lamps => lamps;

        /// <summary>Every lamp's pool corners, flattened. See <see cref="TownLamp.PoolStart"/>.</summary>
        public IReadOnlyList<Vector3> LampPools => lampPools;

        /// <summary>Plan bounds of everything in the town, for a cheap early-out.</summary>
        public Bounds Footprint { get; }

        public int HouseCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < plots.Count; i++)
                {
                    if (plots[i].Kind == TownPlotKind.House)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>True if a point stands on a building, with <paramref name="margin"/> to spare.</summary>
        public bool IsBuiltOn(float x, float z, float margin)
        {
            return Within(x, z, margin, buildingOnly: true);
        }

        /// <summary>True if a point falls on a plot — building or garden.</summary>
        public bool IsOccupied(float x, float z, float margin)
        {
            return Within(x, z, margin, buildingOnly: false);
        }

        private bool Within(float x, float z, float margin, bool buildingOnly)
        {
            if (plots.Count == 0)
            {
                return false;
            }

            // The reach has to cover the largest plot as well as the margin: a plot is indexed by where
            // it stands, so one whose centre is two cells away can still reach this point.
            int span = Mathf.Max(1, Mathf.CeilToInt((margin + largestRadius) / cellSize));

            int centreColumn = Mathf.Clamp(Mathf.FloorToInt((x - gridOrigin.x) / cellSize), 0, columns - 1);
            int centreRow = Mathf.Clamp(Mathf.FloorToInt((z - gridOrigin.y) / cellSize), 0, rows - 1);

            int minColumn = Mathf.Max(0, centreColumn - span);
            int maxColumn = Mathf.Min(columns - 1, centreColumn + span);
            int minRow = Mathf.Max(0, centreRow - span);
            int maxRow = Mathf.Min(rows - 1, centreRow + span);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int cell = row * columns + column;
                    for (int slot = cellStart[cell]; slot < cellStart[cell + 1]; slot++)
                    {
                        Plot plot = plots[cellItems[slot]];
                        float dx = plot.Position.x - x;
                        float dz = plot.Position.z - z;
                        float reach = (buildingOnly ? plot.BuildingRadius : plot.Radius) + margin;

                        if (dx * dx + dz * dz <= reach * reach)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    /// <summary>Counts from a town pass, for the build log.</summary>
    public sealed class TownStats
    {
        public int Houses;
        public int Mosques;
        public int TownHalls;
        public int Fountains;
        public int Stalls;
        public int Windmills;
        public int Barns;
        public int Sawmills;
        public int Fences;
        public int Lamps;

        /// <summary>Pools of light on the carriageway. One per street lamp; the trunk lamps have none.</summary>
        public int Pools;

        public int Cars;
        public int Triangles;

        /// <summary>
        /// Triangles in each of the two glass submeshes.
        ///
        /// <para>The one thing about the night a night render cannot tell you. A town where the
        /// <c>litChance</c> plumbing quietly failed and every pane is being rolled at a flat 50 % looks
        /// entirely plausible in a screenshot; the giveaway is the number, and only the number.</para>
        ///
        /// <para>Counted in triangles rather than in panes because <see cref="BuildingMeshes.GlassSubmesh"/>
        /// is a static helper with nothing to count into. Panes are two triangles each with the exception
        /// of the windmill's, which are boxes, so the ratio is a good proxy and not an exact one.</para>
        /// </summary>
        public int LitGlass;

        public int DarkGlass;

        /// <summary>
        /// Faces that had to be turned round on the way into the buffer. Must be zero.
        ///
        /// Anything else means a helper in <see cref="BuildingMeshes"/> or <see cref="MillMeshes"/> is
        /// authoring its corners in an order that disagrees with the direction it says the face looks in.
        /// The mesh comes out right either way — this is the cheap tripwire that says which helper to go
        /// and read, and it is here because the town once shipped with every wall inside out.
        /// </summary>
        public int Flips;

        public readonly List<int> Submeshes = new List<int>(BuildingMeshes.SubmeshCount);

        public void Add(TownStats other)
        {
            Houses += other.Houses;
            Mosques += other.Mosques;
            TownHalls += other.TownHalls;
            Fountains += other.Fountains;
            Stalls += other.Stalls;
            Windmills += other.Windmills;
            Barns += other.Barns;
            Sawmills += other.Sawmills;
            Fences += other.Fences;
            Lamps += other.Lamps;
            Pools += other.Pools;
            Cars += other.Cars;
            Triangles += other.Triangles;
            LitGlass += other.LitGlass;
            DarkGlass += other.DarkGlass;
            Flips += other.Flips;
        }
    }

    /// <summary>
    /// Works out what stands where in the town: a plot for every frontage along every street.
    ///
    /// <para>Runs after <see cref="MountainField"/> and after the street network, because a plot needs
    /// both — the streets to face and the finished terrain to stand on. The streets themselves come from
    /// <see cref="StreetNetwork"/> now; this used to lay out two lanes of its own, and the difference
    /// between two lanes and a graph is most of the difference between a village and a town.</para>
    ///
    /// <para><b>Transitional.</b> Plots are still ranged along each street independently at a fixed
    /// spacing, which is why the corner keep-outs are crude and why nothing here knows what a block is.
    /// Block-based parcelling replaces the lot.</para>
    /// </summary>
    public static class TownPlanner
    {
        /// <summary>
        /// Adds a path's own centreline to a level-sample list.
        ///
        /// The apron grid takes its heights from the *main* road at the matching distance, which is right
        /// for open ground and wrong under a lane: a lane runs its own grade, so 100 m out it sat almost a
        /// metre above the shelf the apron had laid, and the ribbon stood on a plinth with daylight under
        /// its edge. Sampling the lane itself makes the ground follow it exactly.
        /// </summary>
        public static void AddPathSamples(IRoadPath path, float spacing, List<Vector3> into)
        {
            if (path == null || into == null)
            {
                return;
            }

            int steps = Mathf.Max(2, Mathf.CeilToInt(path.Length / Mathf.Max(1f, spacing)) + 1);
            for (int i = 0; i < steps; i++)
            {
                into.Add(path.GetPositionAtDistance(path.Length * i / (steps - 1)));
            }
        }

        /// <summary>
        /// What gets built along a street, decided by which quarter the block behind it belongs to.
        ///
        /// <para>This is where a quarter stops being a label and becomes something you can see from the
        /// car. An old-town street has narrow plots almost on the pavement; a housing street has wide
        /// ones set well back behind gardens; industry has a few very large ones further back still. The
        /// numbers matter more than any amount of variation within a plot, because what reads at driving
        /// speed is rhythm and setback, not detail.</para>
        /// </summary>
        private readonly struct QuarterStyle
        {
            /// <summary>How far the house stands back from the kerb, metres.</summary>
            public readonly float Setback;

            /// <summary>Spacing of plots along the frontage, metres.</summary>
            public readonly float Spacing;

            /// <summary>Depth of a plot away from the street, metres.</summary>
            public readonly float Depth;

            /// <summary>Chance a plot is left empty, so the row is not extruded.</summary>
            public readonly float Vacancy;

            /// <summary>
            /// Fraction of a building's panes that light after dark.
            ///
            /// The quarter is the right place for it because "how awake is this street at night" is a
            /// property of the street rather than of the house on it. A market frontage of shops is lit
            /// nearly twice as much as a housing street, and a yard full of warehouses is nearly dark —
            /// which, on the night render, is most of what tells the two apart.
            /// </summary>
            public readonly float LitChance;

            public QuarterStyle(float setback, float spacing, float depth, float vacancy, float litChance)
            {
                Setback = setback;
                Spacing = spacing;
                Depth = depth;
                Vacancy = vacancy;
                LitChance = litChance;
            }

            public static QuarterStyle For(TownQuarter quarter)
            {
                switch (quarter)
                {
                    case TownQuarter.OldTown:
                        return new QuarterStyle(4.5f, 12f, 16f, 0.10f, 0.45f);

                    case TownQuarter.Market:
                        return new QuarterStyle(3.0f, 11f, 16f, 0.06f, 0.60f);

                    case TownQuarter.Industry:
                        return new QuarterStyle(12f, 30f, 26f, 0.30f, 0.15f);

                    case TownQuarter.Green:
                        return new QuarterStyle(11f, 26f, 20f, 0.55f, 0.30f);

                    default:
                        return new QuarterStyle(9f, 18f, 20f, 0.20f, 0.35f);
                }
            }

            /// <summary>True where the streets are lit at the closer of the two spacings.</summary>
            public static bool IsCore(TownQuarter quarter)
            {
                return quarter == TownQuarter.OldTown || quarter == TownQuarter.Market;
            }
        }

        /// <summary>
        /// Works out where every plot, lamp and parked car goes.
        ///
        /// Run once, after the terrain exists — plots are seated with
        /// <see cref="TerrainTileBuilder.SampleSurface"/> rather than with the raw height field, because
        /// the mesh is a 12 m linear interpolation of the field and a house placed against the field
        /// itself sinks a corner into the ground on any slope. The same trap the plants were in.
        ///
        /// <para>Frontage is laid out per <i>side</i> of a street rather than per street, because the two
        /// sides of a street belong to different blocks and can be in different quarters. That is what a
        /// high street with shops on one side and the backs of housing on the other is.</para>
        /// </summary>
        public static TownPlan Plan(
            StreetNetwork network,
            StreetIndex index,
            MountainField field,
            in TerrainShape terrainShape,
            in TownShape shape,
            IRoadPath trunk,
            IReadOnlyList<TownBlock> blocks,
            int[] blockOfHalfEdge)
        {
            var plots = new List<TownPlan.Plot>(400);
            var lamps = new List<TownLamp>(128);
            var lampPools = new List<Vector3>(128 * TownLamp.PoolCorners);

            float side = shape.Side;

            // One windmill, not one per industrial street. It is a landmark, and five of them are
            // scenery — the whole point of the thing is that there is one of it.
            bool millPlaced = false;

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];

                // From one junction pad to the other, with a corner keep-out at each end so nothing
                // stands in a sight line — and so two streets' frontages cannot collide at a corner.
                float keepOut = edge.HalfOuter + 4f;
                float from = edge.TrimStart + keepOut;
                float to = edge.Length - edge.TrimEnd - keepOut;

                for (int s = 0; s < 2; s++)
                {
                    // s = 0 is the path's own left. The block on that side decides the quarter; where
                    // there is no block — the outer edge of the town, or a dead-end spur with nothing
                    // behind it — the street's own quarter stands in.
                    bool left = s == 0;
                    int face = FaceOn(network, blockOfHalfEdge, i, left);

                    // Nothing fronts *into* a square. The buildings that address one stand across the
                    // street from it, and the open space is the whole point — a market place with a row
                    // of back gardens down the middle of it is a block with a hole in it.
                    if (face >= 0 && face < blocks.Count && blocks[face].IsSquare)
                    {
                        continue;
                    }

                    TownQuarter quarter = face >= 0 && face < blocks.Count
                        ? blocks[face].Quarter
                        : edge.Quarter;

                    QuarterStyle style = QuarterStyle.For(quarter);

                    bool allowMill = !millPlaced && left && edge.Quarter == TownQuarter.Industry;

                    int before = plots.Count;
                    AddFrontage(
                        plots, edge, field, terrainShape, shape, style, from, to,
                        i * 2 + s, left, allowMill);

                    for (int p = before; !millPlaced && p < plots.Count; p++)
                    {
                        millPlaced = plots[p].Kind == TownPlotKind.Windmill;
                    }
                }

                AddStreetLamps(lamps, lampPools, edge, shape, i);
            }

            AddMosque(plots, trunk, index, field, terrainShape, shape);

            for (int i = 0; i < network.Squares.Count; i++)
            {
                AddSquareFurniture(plots, network, network.Squares[i], index);
                AddTownHall(plots, network, network.Squares[i], field, terrainShape);
            }

            // Lamps down the trunk road through the town, on the town side only. Poolless — see
            // TownLamp — because the pass is textured asphalt and a flat day colour cannot vanish into it.
            for (float along = shape.AlongStart; along <= shape.AlongEnd; along += shape.LampSpacing)
            {
                float clamped = Mathf.Clamp(along, 0f, trunk.Length);
                Vector3 centre = trunk.GetPositionAtDistance(clamped);
                Vector3 right = trunk.GetRightAtDistance(clamped);

                Vector3 at = centre + right * (RoadShape.Default.OuterHalfWidth + 1.6f) * side;
                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                    out Vector3 point, out Vector3 _);

                lamps.Add(new TownLamp(
                    point, HeadingOf(-right * side), Hash(4211, Mathf.RoundToInt(along), 0), 0, 0));
            }

            // Each frontage was laid out against its own street and knows nothing about the others, so a
            // plot fronting one street can sit squarely in another that crosses behind it. Cheaper to
            // drop those here than to make every frontage aware of every street — and with the spatial
            // index it is one cell lookup per plot rather than a walk down every centreline.
            ClearStreets(plots, index, trunk, shape);

            var footprint = new Bounds(
                plots.Count > 0 ? plots[0].Position : Vector3.zero, Vector3.one);
            for (int i = 0; i < plots.Count; i++)
            {
                footprint.Encapsulate(new Bounds(plots[i].Position, Vector3.one * (plots[i].Radius * 2f)));
            }

            return new TownPlan(plots, lamps, lampPools, footprint);
        }

        /// <summary>How far the pool of light floats above the carriageway, metres.</summary>
        private const float PoolLift = 0.03f;

        /// <summary>Radius of a pool of light on the road, metres. Three metres across.</summary>
        private const float PoolRadius = 1.5f;

        /// <summary>
        /// Lamps down one street: alternating sides, on the footway, clear of both junction pads.
        ///
        /// <para>Alternating rather than lining both kerbs. Two rows of lamps down a 7 m street is a
        /// motorway interchange; one row that crosses over is what a street looks like, and it halves both
        /// the triangles and the number of pools competing for the same patch of road.</para>
        ///
        /// <para>The keep-out at each end is the junction pad plus a little, the same reasoning as the
        /// frontage above: a lamp standing in a pad is a lamp in the middle of a crossroads.</para>
        /// </summary>
        private static void AddStreetLamps(
            List<TownLamp> lamps,
            List<Vector3> pools,
            StreetEdge edge,
            in TownShape shape,
            int edgeIndex)
        {
            if (edge.Path == null)
            {
                return;
            }

            float keepOut = edge.HalfOuter + 3f;
            float from = edge.TrimStart + keepOut;
            float to = edge.Length - edge.TrimEnd - keepOut;
            if (to <= from)
            {
                return;
            }

            float spacing = Mathf.Max(8f,
                QuarterStyle.IsCore(edge.Quarter) ? shape.LampSpacingCore : shape.LampSpacingOuter);

            TownStreetShape section = edge.Shape;

            // Where along the footway the post stands, and how high its foot is: both straight off the
            // street's own cross-section, so a lamp cannot end up buried in the pavement or floating over
            // it whatever the kerb height of that street kind happens to be.
            float postAcross = section.HalfWidth + section.KerbFace + section.FootwayWidth * 0.5f;
            float footwayRise = section.SurfaceLift + section.KerbHeight;

            // Odd streets start on the other side, so two parallel streets do not line their lamps up.
            bool left = (edgeIndex & 1) == 0;
            int index = 0;

            for (float along = from; along <= to; along += spacing, left = !left, index++)
            {
                float sign = left ? -1f : 1f;

                Vector3 position = TownStreetBuilder.PointAcross(
                    edge.Path, section, along, postAcross * sign, footwayRise);

                Vector3 right = edge.Path.GetRightAtDistance(Mathf.Clamp(along, 0f, edge.Path.Length));

                int poolStart = pools.Count;
                AddPoolCorners(pools, edge, section, along, sign);

                lamps.Add(new TownLamp(
                    position,
                    HeadingOf(-right * sign),
                    Hash(5501, edgeIndex, index),
                    poolStart,
                    TownLamp.PoolCorners));
            }
        }

        /// <summary>
        /// The hexagon of light on the carriageway under one lamp, corner by corner.
        ///
        /// <para>Each corner is seated on the street's cross-section at its <i>own</i> distance from the
        /// centreline, which is the whole reason this is not a flat disc: the carriageway is crowned 6 cm
        /// and a flat polygon would cut through it near the middle of the road and float at the gutter.
        /// The crown is linear from the crown line to the gutter, and so is this.</para>
        ///
        /// <para>Placed just inside the near kerb rather than under the post, because the lantern hangs
        /// out over the road — and the pool is sized so its outer edge stops a few centimetres short of
        /// the gutter, where the kerb face would otherwise cut through it.</para>
        /// </summary>
        private static void AddPoolCorners(
            List<Vector3> pools,
            StreetEdge edge,
            in TownStreetShape section,
            float along,
            float sign)
        {
            float centreAcross = sign * (section.HalfWidth - PoolRadius - 0.05f);

            for (int i = 0; i < TownLamp.PoolCorners; i++)
            {
                float angle = i * (Mathf.PI * 2f / TownLamp.PoolCorners);
                float across = centreAcross + Mathf.Cos(angle) * PoolRadius;
                float at = along + Mathf.Sin(angle) * PoolRadius;

                float toGutter = Mathf.Clamp01(Mathf.Abs(across) / Mathf.Max(0.1f, section.HalfWidth));
                float rise = section.SurfaceLift + section.Crown * (1f - toGutter) + PoolLift;

                pools.Add(TownStreetBuilder.PointAcross(edge.Path, section, at, across, rise));
            }
        }

        /// <summary>
        /// Puts the mosque on the highest ground the town has.
        ///
        /// <para>Not by eye and not at a hand-picked coordinate: the basin's cross-fall lifts the floor
        /// four and a half metres from the trunk road out to the far edge, so the highest ground is
        /// wherever <see cref="TownShape.FloorHeight"/> says it is, and the search asks it. Height is
        /// what a landmark trades on — every metre of ground under the minaret is a metre of minaret seen
        /// from the pass — so the one decision that matters here is made by measurement.</para>
        ///
        /// <para>It faces the trunk road rather than the street it stands on, because a minaret is read
        /// from the road that passes the town, not from the lane behind it.</para>
        /// </summary>
        private static void AddMosque(
            List<TownPlan.Plot> plots,
            IRoadPath trunk,
            StreetIndex index,
            MountainField field,
            in TerrainShape terrainShape,
            in TownShape shape)
        {
            float bestAlong = 0f;
            float bestAcross = 0f;
            float bestHeight = float.MinValue;

            // Off the back of the town, where the cross-fall has done its work, and clear of the streets.
            for (float along = shape.AlongStart + 80f; along <= shape.AlongEnd - 80f; along += 15f)
            {
                for (float across = shape.AcrossOuter * 0.62f; across <= shape.AcrossOuter * 0.94f;
                     across += 15f)
                {
                    Vector3 at = TownShape.ToWorld(trunk, shape, along, across);

                    // Not in a street, and not so far from one that the mosque has no way in.
                    float toStreet = index.DistanceTo(at.x, at.z, out int _);
                    if (toStreet < 22f || toStreet > 55f)
                    {
                        continue;
                    }

                    if (at.y > bestHeight)
                    {
                        bestHeight = at.y;
                        bestAlong = along;
                        bestAcross = across;
                    }
                }
            }

            if (bestHeight <= float.MinValue)
            {
                Debug.LogWarning("[Horizon] Town: nowhere to put the mosque. Every candidate was either "
                                 + "in a street or too far from one to reach.");
                return;
            }

            Vector3 site = TownShape.ToWorld(trunk, shape, bestAlong, bestAcross);
            TerrainTileBuilder.SampleSurface(field, terrainShape, site.x, site.z,
                out Vector3 point, out Vector3 _);

            Vector3 towards = trunk.GetPositionAtDistance(
                Mathf.Clamp(bestAlong, 0f, trunk.Length)) - point;
            towards.y = 0f;

            // 0.7, but the minaret's own openings ignore it and are always lit — see LandmarkMeshes.
            plots.Add(new TownPlan.Plot(
                point, HeadingOf(towards), 16f, 20f, TownPlotKind.Mosque, false, false, 0.7f,
                Hash(7717, Mathf.RoundToInt(bestAlong), 0)));
        }

        /// <summary>
        /// A fountain at the middle of a square, and a scatter of stalls over the rest of it.
        ///
        /// <para>Everything here is seated on <see cref="TownSquare.PavedHeightAt"/> rather than on the
        /// terrain, because the paving stands a kerb height above the ground it covers — a stall placed
        /// against the height field stands 16 cm into the flagstones.</para>
        ///
        /// <para>Candidates are rejected against the street index up front rather than left for
        /// <see cref="ClearStreets"/> to sweep up afterwards. Both would work, but a stall dropped after
        /// placement leaves a gap in the pattern the placement was trying to make, and the count would
        /// then depend on how many happened to survive.</para>
        /// </summary>
        private static void AddSquareFurniture(
            List<TownPlan.Plot> plots, StreetNetwork network, TownSquare square, StreetIndex index)
        {
            if (square.Interior == null || square.Interior.Length < 3)
            {
                return;
            }

            var random = new PlantRandom(Hash(3301, square.Index, 0));

            plots.Add(new TownPlan.Plot(
                square.Centre, 0f, 4f, 4f, TownPlotKind.Fountain, false, false, 0f, random.NextSeed()));

            // Clear of the fountain, clear of the surrounding carriageways, and inside the paving. A
            // rejection sample rather than a grid: the square is not rectangular, and a grid clipped to a
            // polygon reads as a grid with its corners bitten off.
            const float fountainKeepOut = 7f;
            const float streetKeepOut = 12f;
            int wanted = 8 + Mathf.FloorToInt(random.Range(0f, 7f));
            int placed = 0;

            var bounds = new Bounds(square.Interior[0], Vector3.zero);
            for (int i = 1; i < square.Interior.Length; i++)
            {
                bounds.Encapsulate(square.Interior[i]);
            }

            for (int attempt = 0; attempt < wanted * 30 && placed < wanted; attempt++)
            {
                float x = random.Range(bounds.min.x, bounds.max.x);
                float z = random.Range(bounds.min.z, bounds.max.z);
                var at = new Vector3(x, 0f, z);

                if (!StreetNetwork.Contains(square.Interior, at)
                    || index.IsWithin(x, z, streetKeepOut))
                {
                    continue;
                }

                float dx = x - square.Centre.x;
                float dz = z - square.Centre.z;
                if (dx * dx + dz * dz < fountainKeepOut * fountainKeepOut)
                {
                    continue;
                }

                if (TooClose(plots, x, z, 4.5f))
                {
                    continue;
                }

                plots.Add(new TownPlan.Plot(
                    new Vector3(x, square.PavedHeightAt(x, z), z),
                    random.Range(0f, 360f),
                    2.5f, 2.5f, TownPlotKind.Stall, false, false, 0f, random.NextSeed()));

                placed++;
            }

            if (placed < 6)
            {
                Debug.LogWarning($"[Horizon] Square '{square.Name}' took only {placed} stalls. Either it "
                                 + "is too small for the keep-outs, or its paved boundary has come out "
                                 + "inside out and the point-in-polygon test is rejecting the middle.");
            }
        }

        /// <summary>Whether any plot already stands within a radius of a point, in plan.</summary>
        private static bool TooClose(List<TownPlan.Plot> plots, float x, float z, float radius)
        {
            for (int i = 0; i < plots.Count; i++)
            {
                float dx = plots[i].Position.x - x;
                float dz = plots[i].Position.z - z;

                if (dx * dx + dz * dz < radius * radius)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The town hall, across the street from the uphill edge of a square and facing back into it.
        ///
        /// <para>Uphill because that is where a civic building goes: it is the one frontage on the square
        /// that is seen against the sky from the other side rather than against the buildings behind it.
        /// Which edge that is comes from <see cref="TownSquare.UphillEdge"/>, measured off the built
        /// geometry — naming it in the layout table would be a number that quietly stopped being true the
        /// first time the square moved.</para>
        ///
        /// <para>Anything the frontage pass has already put on that spot is removed first. The alternative
        /// is placing the hall before the frontages and teaching those to avoid it, which would mean the
        /// frontage code carrying a special case for every landmark there will ever be.</para>
        /// </summary>
        private static void AddTownHall(
            List<TownPlan.Plot> plots,
            StreetNetwork network,
            TownSquare square,
            MountainField field,
            in TerrainShape terrainShape)
        {
            if (square.Edges == null || square.Edges.Length == 0)
            {
                return;
            }

            StreetEdge edge = network.Edges[square.Edges[square.UphillEdge]];
            if (edge.Path == null)
            {
                return;
            }

            float at = edge.Length * 0.5f;
            Vector3 centre = edge.Path.GetPositionAtDistance(at);
            Vector3 right = edge.Path.GetRightAtDistance(at);

            // Away from the square: the hall stands on the far side of the street that edges it.
            float sign = -Mathf.Sign(Vector3.Dot(right, square.Centre - centre));

            const float halfWidth = 12f;
            const float halfDepth = 8f;

            // Close to the pavement — 2 m, against the 9 m an ordinary house on a housing street stands
            // back. A civic building addresses its square; one set back behind a front garden is a large
            // villa. It is also the only setback the land allows: the strip between the square and the
            // housing row behind it is under twenty metres deep.
            float setback = edge.HalfOuter + 2f + halfDepth;

            Vector3 site = centre + right * (setback * sign);
            TerrainTileBuilder.SampleSurface(field, terrainShape, site.x, site.z,
                out Vector3 point, out Vector3 _);

            for (int i = plots.Count - 1; i >= 0; i--)
            {
                float dx = plots[i].Position.x - point.x;
                float dz = plots[i].Position.z - point.z;

                if (dx * dx + dz * dz < 20f * 20f)
                {
                    plots.RemoveAt(i);
                }
            }

            plots.Add(new TownPlan.Plot(
                point, HeadingOf(-right * sign), halfWidth, halfDepth,
                TownPlotKind.TownHall, false, false, 0.75f,
                Hash(6151, square.Index, 0)));
        }

        /// <summary>Which block lies on one side of a street, or -1 for the open edge of the town.</summary>
        private static int FaceOn(
            StreetNetwork network, int[] blockOfHalfEdge, int edgeIndex, bool left)
        {
            bool forwardHalfIsLeft = network.BlocksLieLeftOfWalk;
            int half = (edgeIndex << 1) | (left == forwardHalfIsLeft ? 0 : 1);
            return blockOfHalfEdge[half];
        }

        /// <summary>
        /// Drops any plot standing in a street it does not face.
        ///
        /// <para>A plot is always at least its street's setback from the street it fronts, so a threshold
        /// well under that only catches the ones sitting on a <i>different</i> street — which happens all
        /// the time when frontages are laid out one street at a time and forty of them cross.</para>
        ///
        /// <para>Through <see cref="StreetIndex"/> rather than by walking every centreline. The old
        /// version was plots times streets times samples-per-street: at four hundred plots and forty
        /// streets that is a million arc-length lookups, and it was comfortably the most expensive thing
        /// in the build.</para>
        /// </summary>
        private static void ClearStreets(
            List<TownPlan.Plot> plots, StreetIndex index, IRoadPath trunk, in TownShape shape)
        {
            float trunkClearance = RoadShape.Default.OuterHalfWidth + 6f;

            for (int i = plots.Count - 1; i >= 0; i--)
            {
                Vector3 at = plots[i].Position;

                // Its own street is further away than this, so anything inside it is a street the plot
                // does not face.
                float minimum = shape.PlotSetback * 0.7f;

                bool blocked = index.IsWithin(at.x, at.z, minimum)
                               || PlanDistance(trunk, at) < trunkClearance;

                if (blocked)
                {
                    plots.RemoveAt(i);
                }
            }
        }

        /// <summary>Plan distance from a point to the nearest place on a path.</summary>
        private static float PlanDistance(IRoadPath path, Vector3 point)
        {
            const float step = 5f;
            float best = float.MaxValue;

            for (float along = 0f; along <= path.Length; along += step)
            {
                Vector3 at = path.GetPositionAtDistance(Mathf.Min(along, path.Length));

                float dx = at.x - point.x;
                float dz = at.z - point.z;
                best = Mathf.Min(best, dx * dx + dz * dz);
            }

            return Mathf.Sqrt(best);
        }

        /// <summary>Lines plots up along one side of one street.</summary>
        private static void AddFrontage(
            List<TownPlan.Plot> plots,
            StreetEdge edge,
            MountainField field,
            in TerrainShape terrainShape,
            in TownShape shape,
            in QuarterStyle style,
            float from,
            float to,
            int frontageId,
            bool left,
            bool allowMill)
        {
            if (edge.Path == null || to <= from)
            {
                return;
            }

            float sign = left ? -1f : 1f;
            float setback = edge.HalfOuter + style.Setback + style.Depth * 0.5f;
            int index = 0;

            for (float along = from; along <= to; along += style.Spacing, index++)
            {
                float clamped = Mathf.Clamp(along, 0f, edge.Path.Length);
                Vector3 centre = edge.Path.GetPositionAtDistance(clamped);
                Vector3 right = edge.Path.GetRightAtDistance(clamped);

                // The mill is decided before the vacancy roll, not after. Testing for it further down
                // meant a plot could be left empty before the test ever ran, and the town came out with
                // no landmark at all and nothing saying so.
                bool mill = allowMill && left
                            && Mathf.Abs(along - (from + to) * 0.5f) < style.Spacing * 0.5f;

                var random = new PlantRandom(Hash(frontageId, index, 0));
                if (!mill && random.Chance(style.Vacancy))
                {
                    continue;
                }

                var kind = TownPlotKind.House;
                if (mill)
                {
                    kind = TownPlotKind.Windmill;
                }
                else if (random.Chance(shape.WorkingBuildingChance))
                {
                    // A working building rather than another house. Scattered through the town instead
                    // of pushed to one edge, because a barn between two cottages is what a town actually
                    // looks like.
                    kind = random.Chance(0.6f) ? TownPlotKind.Barn : TownPlotKind.Sawmill;
                }

                bool landmark = kind != TownPlotKind.House;
                float jitter = landmark ? 0f : random.Range(-1.5f, 1.5f);

                Vector3 at = centre + right * (setback * sign)
                             + edge.Path.GetDirectionAtDistance(clamped) * jitter;

                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                    out Vector3 point, out Vector3 _);

                float halfWidth = mill ? 10f : style.Spacing * 0.44f;
                float halfDepth = mill ? 10f : style.Depth * 0.5f;

                plots.Add(new TownPlan.Plot(
                    point,
                    HeadingOf(-right * sign),
                    halfWidth,
                    halfDepth,
                    kind,
                    kind == TownPlotKind.House && random.Chance(shape.ParkedCarChance),
                    kind == TownPlotKind.House && random.Chance(0.55f),
                    style.LitChance,
                    random.NextSeed()));
            }
        }

        /// <summary>
        /// Everything standing on one terrain tile, as a single mesh. Null when the tile holds no plots,
        /// which is almost all of them.
        /// </summary>
        public static Mesh BuildTile(
            TerrainTileKey key,
            in TerrainShape terrainShape,
            in TownShape shape,
            TownPlan plan,
            string meshName,
            out TownStats stats)
        {
            stats = new TownStats();
            if (plan == null)
            {
                return null;
            }

            float tileSize = TerrainTileBuilder.TileSize(terrainShape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            var buffer = new VegetationMeshBuffer(BuildingMeshes.SubmeshCount);

            for (int i = 0; i < plan.Plots.Count; i++)
            {
                TownPlan.Plot plot = plan.Plots[i];
                if (!Owns(plot.Position, originX, originZ, tileSize))
                {
                    continue;
                }

                var place = new PlantPlacement(plot.Position, Vector3.up,
                    plot.Yaw * Mathf.Deg2Rad, 1f, plot.Seed);
                var random = new PlantRandom(plot.Seed);

                switch (plot.Kind)
                {
                    case TownPlotKind.Mosque:
                        LandmarkMeshes.AddMosque(buffer, place, ref random);
                        stats.Mosques++;
                        continue;

                    case TownPlotKind.TownHall:
                        LandmarkMeshes.AddTownHall(buffer, place, ref random);
                        stats.TownHalls++;
                        continue;

                    case TownPlotKind.Fountain:
                        LandmarkMeshes.AddFountain(buffer, place, ref random);
                        stats.Fountains++;
                        continue;

                    case TownPlotKind.Stall:
                        LandmarkMeshes.AddMarketStall(buffer, place, ref random);
                        stats.Stalls++;
                        continue;

                    case TownPlotKind.Windmill:
                        MillMeshes.AddWindmill(buffer, place);
                        stats.Windmills++;
                        continue;

                    case TownPlotKind.Barn:
                        MillMeshes.AddBarn(buffer, place);
                        stats.Barns++;
                        continue;

                    case TownPlotKind.Sawmill:
                        MillMeshes.AddSawmill(buffer, place);
                        stats.Sawmills++;
                        continue;
                }

                BuildingMeshes.AddHouse(buffer, place, plot.LitChance);
                stats.Houses++;

                AddGarden(buffer, place, plot, shape, ref random, stats);

                if (plot.HasCar)
                {
                    var carPlace = new PlantPlacement(plot.Position, Vector3.up,
                        (plot.Yaw + random.Range(-10f, 10f)) * Mathf.Deg2Rad, 1f, random.NextSeed());

                    // Parked on the street side of the house, clear of the front door.
                    Vector3 offset = carPlace.Right * (plot.HalfWidth * 0.62f) + carPlace.Forward * 3.5f;
                    var parked = new PlantPlacement(plot.Position + offset, Vector3.up,
                        (plot.Yaw + 90f) * Mathf.Deg2Rad, 1f, random.NextSeed());

                    BuildingMeshes.AddParkedCar(buffer, parked);
                    stats.Cars++;
                }
            }

            for (int i = 0; i < plan.Lamps.Count; i++)
            {
                TownLamp lamp = plan.Lamps[i];
                if (!Owns(lamp.Position, originX, originZ, tileSize))
                {
                    continue;
                }

                var place = new PlantPlacement(
                    lamp.Position, Vector3.up, lamp.Yaw * Mathf.Deg2Rad, 1f, lamp.Seed);
                BuildingMeshes.AddStreetLamp(buffer, place);
                stats.Lamps++;

                // The pool goes on the tile the *lamp* is on, even where a corner of it strays over a
                // tile boundary. Three metres of polygon on the wrong side of a seam is nothing; a pool
                // split between two tiles that stream independently is a half-lit road.
                if (lamp.PoolCount > 0)
                {
                    BuildingMeshes.AddGroundPool(
                        buffer, plan.LampPools, lamp.PoolStart, lamp.PoolCount);
                    stats.Pools++;
                }
            }

            stats.Triangles = buffer.TriangleCount;
            stats.Flips = buffer.FlipCount;
            stats.LitGlass = buffer.TriangleCountIn(BuildingMeshes.WindowLitSubmesh);

            // Counted before the merge, not after: the dark glass is about to stop being a submesh of its
            // own and become a colour, and the lit-versus-dark split is the one thing about the town at
            // night that a night render cannot tell you.
            stats.DarkGlass = buffer.TriangleCountIn(BuildingMeshes.WindowDarkSubmesh);

            // Ten of the twelve categories fold into one submesh here, carrying their colour in their
            // vertices. Everything above this line is written exactly as it was.
            buffer.MergeTinted(BuildingMeshes.OpaqueTints());

            return buffer.ToMesh(meshName, stats.Submeshes);
        }

        /// <summary>A hedge or a fence around the front of a plot, and a couple of garden bushes.</summary>
        private static void AddGarden(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            in TownPlan.Plot plot,
            in TownShape shape,
            ref PlantRandom random,
            TownStats stats)
        {
            float frontZ = plot.HalfDepth;
            float halfWidth = plot.HalfWidth * 0.92f;

            if (plot.Fenced)
            {
                BuildingMeshes.AddFence(buffer, place, 0f, frontZ, halfWidth, 0f, ref random);
                stats.Fences++;
            }
            else
            {
                BuildingMeshes.AddHedge(buffer, place, 0f, frontZ, halfWidth, 0f, ref random);
            }

            // Side boundaries, one or both, so plots read as separate gardens rather than one long lawn.
            if (random.Chance(0.75f))
            {
                BuildingMeshes.AddHedge(buffer, place, halfWidth, frontZ * 0.35f, 0f,
                    plot.HalfDepth * 0.6f, ref random);
            }

            if (random.Chance(0.75f))
            {
                BuildingMeshes.AddHedge(buffer, place, -halfWidth, frontZ * 0.35f, 0f,
                    plot.HalfDepth * 0.6f, ref random);
            }
        }

        /// <summary>Which tile a plot belongs to, decided by where it stands.</summary>
        private static bool Owns(Vector3 position, float originX, float originZ, float tileSize)
        {
            return position.x >= originX && position.x < originX + tileSize
                   && position.z >= originZ && position.z < originZ + tileSize;
        }

        /// <summary>FNV-1a with an avalanche, so a plot depends on nothing but which plot it is.</summary>
        private static uint Hash(int a, int b, int c)
        {
            unchecked
            {
                uint hash = 2166136261u;
                hash = (hash ^ (uint)a) * 16777619u;
                hash = (hash ^ (uint)b) * 16777619u;
                hash = (hash ^ (uint)c) * 16777619u;

                hash ^= hash >> 15;
                hash *= 2246822519u;
                hash ^= hash >> 13;
                hash *= 3266489917u;
                hash ^= hash >> 16;
                return hash;
            }
        }

        /// <summary>Heading in the builder's convention: 0 faces +Z, increasing turns towards +X.</summary>
        private static float HeadingOf(Vector3 direction)
        {
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }
    }
}
