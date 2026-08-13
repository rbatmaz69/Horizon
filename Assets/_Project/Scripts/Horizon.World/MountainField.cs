using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The terrain height field around a road, and the spatial index that makes it affordable.
    ///
    /// Two things are combined here, and keeping them separate is the whole point:
    ///
    /// **The shelf** is an inverse-distance weighted average of the road samples near a point. It exists so
    /// the ground meets the carriageway exactly, and so the ground *between* two stacked switchback legs
    /// slopes from the lower one up to the higher one. Taking the height from the single nearest sample
    /// instead — the first version of this — put a hard step down the middle of every switchback, precisely
    /// where a retaining wall belongs. The weight is an inverse fifth power: at the cube a leg twenty metres
    /// below was still lifting the ground almost a metre only ten metres from the road, which crowded the
    /// carriageway with hillside.
    ///
    /// **The mountain** is a smooth field that knows nothing about any *individual* piece of road: the road
    /// system's own elevation seen from far enough away that single legs disappear, plus noise. The road is
    /// then carved into it.
    ///
    /// An earlier attempt built the mountain by extrapolating the *gradient* of the shelf outwards. That
    /// failed badly and is worth recording: the sharp weighting makes the shelf nearly stepped close to a
    /// sample, so its numerical gradient is enormous and differs wildly between neighbouring grid points —
    /// and it was multiplied by a distance running out to the full corridor width. The result was a field of
    /// thin spires and vertical walls rather than a mountain.
    ///
    /// There is deliberately **nothing about tunnels here**. Cutting a hole in a height field for a bore and
    /// closing it again with separately computed lids and walls was tried at length and abandoned: a height
    /// field is quantised to its cell size, a bore is not, and the two shapes never matched — every fix moved
    /// the mismatch somewhere else. Tunnels are now closed bodies standing on an untouched surface; see
    /// <see cref="TunnelBuilder"/>.
    /// </summary>
    public sealed class MountainField
    {
        /// <summary>
        /// One carriageway handed to the field, and the course it came from if it has bridges.
        ///
        /// <para>The course is optional and is read for one thing only: which stretches are carried on
        /// piers rather than laid on the ground. A road without one is a road the terrain rises to meet
        /// everywhere, which is what every road in the world was until there was a motorway.</para>
        /// </summary>
        public readonly struct FieldRoad
        {
            public readonly IRoadPath Path;

            /// <summary>May be null. Only <c>IsBridged</c> is read from it.</summary>
            public readonly RoadCourse Course;

            public FieldRoad(IRoadPath path, RoadCourse course = null)
            {
                Path = path;
                Course = course;
            }
        }

        /// <summary>Samples further away than this contribute nothing to the shelf.</summary>
        private const float InfluenceRadius = 80f;

        /// <summary>Cell size of the lookup grid.</summary>
        private const float BucketSize = 32f;

        /// <summary>Cell size of the coarse mountain grid, metres.</summary>
        private const float CoarseCellSize = 40f;

        /// <summary>How far the coarse grid reaches for samples. Large, so the result is low-frequency.</summary>
        private const float CoarseReach = 250f;

        /// <summary>Margin around the road that the coarse grid covers, metres.</summary>
        private const float CoarseMargin = 320f;

        /// <summary>Smoothing passes over the coarse grid.</summary>
        private const int CoarseSmoothingPasses = 4;

        private readonly Vector3[] samples;
        private readonly TerrainShape shape;

        /// <summary>
        /// Which samples pull the ground up to meet them. False only for road on a bridge.
        ///
        /// <para>This is the whole of what makes a bridge a bridge rather than an embankment. The shelf
        /// follows the nearest carriageway everywhere, so a road taken across a valley would simply
        /// carry the valley floor up with it and there would be nothing to span. A bridge sample stays
        /// in <see cref="DistanceToRoad"/> — the tiles, the vegetation falloff and the rails all still
        /// need to know there is a road overhead — but contributes nothing to the shelf, so the terrain
        /// beneath it is whatever the mountain was going to be.</para>
        ///
        /// <para>It also has to be excluded from the shelf's <i>nearest</i> distance, not only from its
        /// weighted average. That distance is what <see cref="HeightAt"/> blends on: if a bridge sample
        /// still answered "the road is nought metres away", the point under the deck would sit on the
        /// flat shelf regardless of the weights, and the valley would come back as a mesa. Excluding it
        /// from both is why the abutments fair in on their own — the nearest shelf-carrying sample there
        /// is the road on solid ground at each end.</para>
        /// </summary>
        private readonly bool[] carriesShelf;

        /// <summary>
        /// How many of <see cref="samples"/> are the road's own, stored first.
        ///
        /// The split matters. The shelf has to treat a level sample exactly like a piece of road — that is
        /// what levels the ground under a town at all — but <see cref="DistanceToRoad"/> must not, and the
        /// two were the same method once. With a basin of level samples in the field, every point in the
        /// town was zero metres from "road": the terrain corridor swelled by 300 m in every direction, and
        /// the vegetation density falloff, which thins the forest with distance from the carriageway, ran
        /// the whole basin at maximum density. Roughly a quarter of a million triangles of forest, growing
        /// where the town was supposed to go.
        /// </summary>
        private readonly int roadSampleCount;

        /// <summary>The lattice over everything that forms the shelf. See <see cref="carriesShelf"/>.</summary>
        private readonly int[] cellStart;
        private readonly int[] cellItems;

        /// <summary>The same lattice, holding the road samples alone — bridges included.</summary>
        private readonly int[] roadCellStart;
        private readonly int[] roadCellItems;

        /// <summary>The same lattice again, holding only the bridge samples. See <see cref="HeightAt"/>.</summary>
        private readonly int[] bridgeCellStart;
        private readonly int[] bridgeCellItems;

        /// <summary>
        /// Headroom a bridge span guarantees itself, metres — how far the ground is pushed down under a
        /// deck that would otherwise stand in it.
        ///
        /// <para>Needed because of where this world's terrain comes from. The mountain is not a landscape
        /// the road was laid across; it is derived <i>from</i> the road, as its own elevation seen from
        /// far away plus noise of ±31 m. So there is no pre-existing valley to bridge, and a span
        /// authored at a chosen distance has about an even chance of being sunk in a hillside — which is
        /// precisely what the first two viaducts were, at 85 and 224 blocked points of carriageway.</para>
        ///
        /// <para>Rather than hunting for a dip that may not exist, a bridge declares one. Under the deck
        /// the ground is capped at this far below it and left free to fall further wherever the mountain
        /// was going that way anyway — so the span is always a real gap, and its depth is still the
        /// terrain's to decide.</para>
        /// </summary>
        private const float BridgeHeadroom = 9f;

        /// <summary>How far either side of a bridge's centreline that cap applies, metres.</summary>
        private const float BridgeCorridor = 46f;

        private readonly Vector2 gridOrigin;
        private readonly int columns;
        private readonly int rows;

        private readonly float[] coarseHeights;
        private readonly Vector2 coarseOrigin;
        private readonly int coarseColumns;
        private readonly int coarseRows;

        /// <param name="levelSamples">
        /// Extra points the ground should be levelled to, on top of the road's own. They behave exactly
        /// like road samples — the shelf forms around them and the mountain is carved away — which is what
        /// makes a buildable area possible at all.
        ///
        /// A road on its own can only ever flatten a ribbon <see cref="Verge"/> wide, so anything needing
        /// an *area* of level ground has to say where. A village fills its footprint with a coarse grid of
        /// these; the shelves merge into one floor as long as the grid pitch stays under twice the verge.
        /// They carry a height, so the floor can follow the road's grade rather than sitting level in a
        /// world that is not.
        /// </param>
        public MountainField(
            IRoadPath path,
            in TerrainShape terrainShape,
            float sampleSpacing = 4f,
            IReadOnlyList<Vector3> levelSamples = null)
            : this(new[] { new FieldRoad(path) }, terrainShape, sampleSpacing, levelSamples)
        {
        }

        /// <param name="roads">
        /// Every carriageway in the world, and optionally the course each was built from so its bridges
        /// can be found. One field for all of them rather than one field each, and that is not an
        /// optimisation — two fields would each carve a mountain out of their own road and disagree
        /// wherever the two came near, leaving a step down the seam between them. The terrain lattice is
        /// global world space for the same reason.
        /// </param>
        public MountainField(
            IReadOnlyList<FieldRoad> roads,
            in TerrainShape terrainShape,
            float sampleSpacing = 4f,
            IReadOnlyList<Vector3> levelSamples = null)
        {
            shape = terrainShape;

            int roadCount = roads != null ? roads.Count : 0;
            int extra = levelSamples != null ? levelSamples.Count : 0;

            // Counted before anything is written, so the arrays are exact and the road samples land in
            // one block at the front — DistanceToRoad depends on that split.
            int count = 0;
            for (int r = 0; r < roadCount; r++)
            {
                count += SampleCountFor(roads[r].Path, sampleSpacing);
            }

            samples = new Vector3[count + extra];
            carriesShelf = new bool[count + extra];
            roadSampleCount = count;

            int cursor = 0;
            for (int r = 0; r < roadCount; r++)
            {
                FieldRoad road = roads[r];
                float length = road.Path.Length;
                int steps = SampleCountFor(road.Path, sampleSpacing);

                for (int i = 0; i < steps; i++)
                {
                    float distance = length * i / (steps - 1);
                    samples[cursor] = road.Path.GetPositionAtDistance(distance);

                    // A bridge deck is road, but it is road the ground does not reach.
                    carriesShelf[cursor] = road.Course == null || !road.Course.IsBridged(distance);
                    cursor++;
                }
            }

            for (int i = 0; i < extra; i++)
            {
                samples[count + i] = levelSamples[i];
                carriesShelf[count + i] = true;
            }

            // The grid has to be described before the buckets can be filled, because filling them uses
            // CellOf — so these are assigned first and read from there.
            Bounds bounds = RoadBounds;
            gridOrigin = new Vector2(bounds.min.x - InfluenceRadius, bounds.min.z - InfluenceRadius);
            columns = Mathf.Max(1, Mathf.CeilToInt((bounds.size.x + InfluenceRadius * 2f) / BucketSize));
            rows = Mathf.Max(1, Mathf.CeilToInt((bounds.size.z + InfluenceRadius * 2f) / BucketSize));

            BuildBuckets(samples.Length, carriesShelf, out cellStart, out cellItems);
            BuildBuckets(roadSampleCount, null, out roadCellStart, out roadCellItems);

            var onBridge = new bool[samples.Length];
            for (int i = 0; i < roadSampleCount; i++)
            {
                onBridge[i] = !carriesShelf[i];
            }

            BuildBuckets(roadSampleCount, onBridge, out bridgeCellStart, out bridgeCellItems);

            coarseOrigin = new Vector2(bounds.min.x - CoarseMargin, bounds.min.z - CoarseMargin);
            coarseColumns = Mathf.Max(2, Mathf.CeilToInt((bounds.size.x + CoarseMargin * 2f) / CoarseCellSize) + 1);
            coarseRows = Mathf.Max(2, Mathf.CeilToInt((bounds.size.z + CoarseMargin * 2f) / CoarseCellSize) + 1);
            coarseHeights = BuildCoarseField();
        }

        private static int SampleCountFor(IRoadPath path, float sampleSpacing)
        {
            return Mathf.Max(2, Mathf.CeilToInt(path.Length / Mathf.Max(1f, sampleSpacing)) + 1);
        }

        /// <summary>Bounds of the road in plan, without any margin.</summary>
        public Bounds RoadBounds
        {
            get
            {
                var bounds = new Bounds(samples[0], Vector3.zero);
                for (int i = 1; i < samples.Length; i++)
                {
                    bounds.Encapsulate(samples[i]);
                }

                return bounds;
            }
        }

        /// <summary>Effective width of the flat shelf either side of the carriageway.</summary>
        public float Verge => Mathf.Max(shape.VergeWidth, shape.CellSize * 2f);

        /// <summary>Terrain height at a world position.</summary>
        public float HeightAt(float x, float z)
        {
            float shelf = RoadFieldAt(x, z, out float nearest) - shape.RoadShelfDrop;

            float away = nearest - Verge;
            float height = away <= 0f
                ? shelf
                : Mathf.Lerp(
                    shelf,
                    MountainAt(x, z),
                    Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(away / Mathf.Max(1f, shape.BlendDistance))));

            return UnderWater(x, z, UnderBridges(x, z, height));
        }

        /// <summary>
        /// The bodies of water carved into this field. Empty until <see cref="SetWater"/> is called.
        /// </summary>
        private WaterBody[] waters = System.Array.Empty<WaterBody>();

        /// <summary>What is carved into this field, for anything that has to agree with it.</summary>
        public System.Collections.Generic.IReadOnlyList<WaterBody> Water => waters;

        /// <summary>
        /// Hands the field its basins, after they have been solved against it.
        ///
        /// <para><b>Set afterwards rather than passed to the constructor, and that is the whole
        /// ordering trick.</b> A body's surface is derived from the ground around it — the rim is
        /// sampled before anything is dug, so the freeboard is a promise about ground that exists
        /// rather than about ground somebody expected. That makes the field an input to the water and
        /// the water an input to the field, which as a constructor argument is a circle. Building the
        /// field twice would break it too, at the cost of a second 320 ms pass and of every consumer
        /// having to be handed the right one of two nearly identical objects. One field that gains its
        /// basins partway through construction is cheaper and leaves nothing to mix up.</para>
        ///
        /// <para>Everything that reads heights must therefore be built <i>after</i> this — terrain,
        /// vegetation, town planning, the traffic bake. A consumer that sampled earlier has natural
        /// ground, which is trees standing in a lake.</para>
        /// </summary>
        public void SetWater(WaterBody[] bodies)
        {
            waters = bodies ?? System.Array.Empty<WaterBody>();
        }

        /// <summary>
        /// Drops the ground into a basin, and lets the bank climb back out of it.
        ///
        /// <para>Same shape as <see cref="UnderBridges"/> and for the same reasons: eased out over a
        /// bank width so a lake is a dish in the hillside rather than a cylinder punched through it,
        /// and only ever downwards.</para>
        ///
        /// <para><b>A minimum is right inside the water too</b>, which is worth saying because it
        /// looks as though it should not be. Where the natural ground is already below the bed — the
        /// Hochfeld hollow reaches −41.8 m, well under any surface a river there could have — the
        /// minimum keeps the hollow, and the water simply reads deeper over it. That is a pool, not a
        /// hole: the surface still covers it, and the tile builder shades from the same carved ground,
        /// so the colour follows. Overriding to the bed profile instead would fill in a valley the
        /// viaduct was drawn to span.</para>
        /// </summary>
        private float UnderWater(float x, float z, float height)
        {
            for (int i = 0; i < waters.Length; i++)
            {
                WaterBody body = waters[i];

                // The rectangle first. Every terrain corner in the world asks every body, and almost
                // all of those questions have a cheap no.
                if (!body.Near(x, z))
                {
                    continue;
                }

                float outside = body.DistanceOutside(x, z);
                if (outside > body.BankEase)
                {
                    continue;
                }

                if (outside <= 0f)
                {
                    height = Mathf.Min(height, body.BedAt(x, z));
                    continue;
                }

                // In the bank: start at the waterline and ease up to whatever the ground was.
                float ease = Mathf.SmoothStep(0f, 1f, outside / Mathf.Max(1f, body.BankEase));
                height = Mathf.Min(height, Mathf.Lerp(body.SurfaceY, height, ease));
            }

            return height;
        }

        /// <summary>
        /// Pushes the ground down under a bridge deck, and only ever down.
        ///
        /// <para>See <see cref="BridgeHeadroom"/> for why a span has to make its own gap rather than
        /// find one. The cap eases out to nothing at <see cref="BridgeCorridor"/>, so the abutments
        /// fair into the hillside instead of standing at the end of a rectangular trench, and it is a
        /// minimum against the natural height — where the mountain was already falling away, it keeps
        /// falling and the viaduct gets the deep valley it was drawn for.</para>
        /// </summary>
        private float UnderBridges(float x, float z, float height)
        {
            if (bridgeCellItems.Length == 0)
            {
                return height;
            }

            int nearest = NearestWithin(bridgeCellStart, bridgeCellItems, x, z, BridgeCorridor,
                out float distance);

            if (nearest < 0)
            {
                return height;
            }

            float ceiling = samples[nearest].y - BridgeHeadroom;
            float ease = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distance / BridgeCorridor));

            return Mathf.Min(height, Mathf.Lerp(ceiling, height, ease));
        }

        /// <summary>
        /// Nearest sample in a lattice within <paramref name="radius"/>, or -1.
        ///
        /// <para>A bounded cell walk rather than the widening ring of <see cref="NearestInLattice"/>,
        /// because this one runs per terrain vertex and per plant. The ring search is right when the
        /// answer must exist however far away it is; here anything past the radius is not an answer at
        /// all, so searching for it is work thrown away.</para>
        /// </summary>
        private int NearestWithin(
            int[] starts, int[] items, float x, float z, float radius, out float distance)
        {
            float bestSqr = radius * radius;
            int best = -1;

            int minColumn = Mathf.Max(0, ColumnOf(x - radius));
            int maxColumn = Mathf.Min(columns - 1, ColumnOf(x + radius));
            int minRow = Mathf.Max(0, RowOf(z - radius));
            int maxRow = Mathf.Min(rows - 1, RowOf(z + radius));

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int cell = row * columns + column;
                    for (int slot = starts[cell]; slot < starts[cell + 1]; slot++)
                    {
                        int index = items[slot];
                        float dx = samples[index].x - x;
                        float dz = samples[index].z - z;
                        float sqr = dx * dx + dz * dz;

                        if (sqr < bestSqr)
                        {
                            bestSqr = sqr;
                            best = index;
                        }
                    }
                }
            }

            distance = best >= 0 ? Mathf.Sqrt(bestSqr) : float.MaxValue;
            return best;
        }

        /// <summary>
        /// Plan distance to the nearest piece of <b>carriageway</b>, metres — level samples excluded.
        ///
        /// This is what every caller outside the field actually wants: how far a tile, a tree or a boulder
        /// is from a road someone drives on. See <see cref="roadSampleCount"/> for what happens when it
        /// answers with the levelled ground instead.
        ///
        /// A widening ring search over the road grid rather than a fixed neighbourhood, because unlike the
        /// shelf this has no influence radius to stop at: a point out on the open mountain is legitimately
        /// three hundred metres from the nearest road and still has to get a real answer.
        /// </summary>
        public float DistanceToRoad(float x, float z)
        {
            if (roadSampleCount == 0)
            {
                return float.MaxValue;
            }

            NearestInLattice(roadCellStart, roadCellItems, x, z, out float bestSqr);
            return Mathf.Sqrt(bestSqr);
        }

        /// <summary>
        /// Index of the nearest sample held by a lattice, or -1 if it holds none.
        ///
        /// A widening ring search rather than a fixed neighbourhood, because unlike the shelf this has no
        /// influence radius to stop at: a point out on the open mountain is legitimately three hundred
        /// metres from the nearest road and still has to get a real answer.
        /// </summary>
        private int NearestInLattice(int[] starts, int[] items, float x, float z, out float bestSqr)
        {
            bestSqr = float.MaxValue;
            int best = -1;

            if (items.Length == 0)
            {
                return -1;
            }

            int centreColumn = Mathf.Clamp(ColumnOf(x), 0, columns - 1);
            int centreRow = Mathf.Clamp(RowOf(z), 0, rows - 1);
            int maxRing = Mathf.Max(columns, rows);

            for (int ring = 0; ring <= maxRing; ring++)
            {
                ScanRing(starts, items, centreColumn, centreRow, ring, x, z, ref bestSqr, ref best);

                // One found is not one nearest: a sample two rings out can still beat one in the corner of
                // this one. Stop only once the nearest cell of the *next* ring is further than what is in
                // hand.
                float reach = ring * BucketSize;
                if (best >= 0 && reach * reach >= bestSqr)
                {
                    break;
                }
            }

            return best;
        }

        /// <summary>Tests every sample in the cells at Chebyshev distance <paramref name="ring"/>.</summary>
        private void ScanRing(
            int[] starts,
            int[] items,
            int centreColumn,
            int centreRow,
            int ring,
            float x,
            float z,
            ref float bestSqr,
            ref int best)
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
                    for (int slot = starts[cell]; slot < starts[cell + 1]; slot++)
                    {
                        int index = items[slot];
                        float dx = samples[index].x - x;
                        float dz = samples[index].z - z;
                        float distanceSqr = dx * dx + dz * dz;

                        if (distanceSqr < bestSqr)
                        {
                            bestSqr = distanceSqr;
                            best = index;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The mountain on its own: the coarse field plus noise. Smooth everywhere, and completely
        /// independent of where any individual piece of road happens to run.
        /// </summary>
        private float MountainAt(float x, float z)
        {
            float baseHeight = CoarseAt(x, z);

            float ridge = (Mathf.PerlinNoise(x * shape.RidgeScale, z * shape.RidgeScale) - 0.5f)
                          * 2f * shape.RidgeAmplitude;
            float detail = (Mathf.PerlinNoise(x * shape.DetailScale, z * shape.DetailScale) - 0.5f)
                           * 2f * shape.DetailAmplitude;

            return baseHeight + ridge + detail;
        }

        /// <summary>
        /// Builds the coarse mountain grid: the road system's elevation seen from far enough away that
        /// individual legs disappear.
        ///
        /// A large reach and a deliberately gentle weight are what make this low-frequency. The shelf uses a
        /// sharp weight so the road underfoot wins; here the whole point is that it does not, because a
        /// mountain must not have a kink at every carriageway. The smoothing passes remove what survives.
        ///
        /// Deriving the shape from the road rather than inventing a cone means the two cannot disagree: the
        /// road climbs this mountain, so the mountain is whatever shape the road climbed.
        ///
        /// <para><b>This used to be the one un-bucketed loop left in the field</b> — every coarse cell
        /// against every sample — and it was left that way with a note saying it would need the same
        /// treatment <see cref="BuildBuckets"/> gives the shelf past roughly 5000 samples. A motorway is
        /// what crossed that line, and by more than the sample count alone suggests: it stretched the
        /// world from about 930 × 1480 m to 8000 × 1900 m, so the coarse grid grew from some 1400 cells
        /// to some 11 400 at the same time as the samples went from ~1500 to ~5500. The product is what
        /// matters, and it went from 6 million checks to 63 million.</para>
        ///
        /// <para>So it now walks the shelf lattice out to <see cref="CoarseReach"/> instead of the whole
        /// array — 17 × 17 cells of 32 m rather than 5500 samples, and it is the same lattice the shelf
        /// already built, so this costs no extra memory.</para>
        /// </summary>
        private float[] BuildCoarseField()
        {
            var heights = new float[coarseColumns * coarseRows];
            float reachSqr = CoarseReach * CoarseReach;

            for (int row = 0; row < coarseRows; row++)
            {
                for (int column = 0; column < coarseColumns; column++)
                {
                    float x = coarseOrigin.x + column * CoarseCellSize;
                    float z = coarseOrigin.y + row * CoarseCellSize;

                    float weightSum = 0f;
                    float valueSum = 0f;

                    int minColumn = Mathf.Max(0, ColumnOf(x - CoarseReach));
                    int maxColumn = Mathf.Min(columns - 1, ColumnOf(x + CoarseReach));
                    int minRow = Mathf.Max(0, RowOf(z - CoarseReach));
                    int maxRow = Mathf.Min(rows - 1, RowOf(z + CoarseReach));

                    for (int cellRow = minRow; cellRow <= maxRow; cellRow++)
                    {
                        for (int cellColumn = minColumn; cellColumn <= maxColumn; cellColumn++)
                        {
                            int cell = cellRow * columns + cellColumn;
                            for (int slot = cellStart[cell]; slot < cellStart[cell + 1]; slot++)
                            {
                                int index = cellItems[slot];
                                float dx = samples[index].x - x;
                                float dz = samples[index].z - z;
                                float distanceSqr = dx * dx + dz * dz;

                                if (distanceSqr > reachSqr)
                                {
                                    continue;
                                }

                                // The constant in the denominator is what keeps this gentle: without it the
                                // nearest sample would dominate and the mountain would follow every leg.
                                float weight = 1f / (Mathf.Sqrt(distanceSqr) + 40f);
                                weightSum += weight;
                                valueSum += weight * samples[index].y;
                            }
                        }
                    }

                    // Out past the reach — the coarse grid covers CoarseMargin beyond the road, which is
                    // wider than CoarseReach on purpose, so the outermost ring of cells legitimately finds
                    // nothing. A widening ring search rather than a scan of the whole array, for the same
                    // reason as everything above.
                    if (weightSum <= 0f)
                    {
                        int nearest = NearestInLattice(cellStart, cellItems, x, z, out float _);
                        heights[row * coarseColumns + column] = nearest >= 0 ? samples[nearest].y : 0f;
                        continue;
                    }

                    heights[row * coarseColumns + column] = valueSum / weightSum;
                }
            }

            for (int pass = 0; pass < CoarseSmoothingPasses; pass++)
            {
                SmoothCoarse(heights);
            }

            return heights;
        }

        private void SmoothCoarse(float[] heights)
        {
            var copy = (float[])heights.Clone();

            for (int row = 1; row < coarseRows - 1; row++)
            {
                for (int column = 1; column < coarseColumns - 1; column++)
                {
                    int index = row * coarseColumns + column;
                    float sum = copy[index] * 4f
                                + copy[index - 1] + copy[index + 1]
                                + copy[index - coarseColumns] + copy[index + coarseColumns];

                    heights[index] = sum / 8f;
                }
            }
        }

        /// <summary>Bilinear sample of the coarse grid, clamped at its edges.</summary>
        private float CoarseAt(float x, float z)
        {
            float fx = Mathf.Clamp((x - coarseOrigin.x) / CoarseCellSize, 0f, coarseColumns - 1.001f);
            float fz = Mathf.Clamp((z - coarseOrigin.y) / CoarseCellSize, 0f, coarseRows - 1.001f);

            int column = Mathf.FloorToInt(fx);
            int row = Mathf.FloorToInt(fz);
            float tx = fx - column;
            float tz = fz - row;

            float h00 = coarseHeights[row * coarseColumns + column];
            float h10 = coarseHeights[row * coarseColumns + column + 1];
            float h01 = coarseHeights[(row + 1) * coarseColumns + column];
            float h11 = coarseHeights[(row + 1) * coarseColumns + column + 1];

            return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
        }

        /// <summary>
        /// Inverse-distance weighted road level at a position, and the distance to the nearest sample. Falls
        /// back to a plain nearest-sample search when nothing is in range, so the field is defined everywhere.
        ///
        /// <para>Both numbers come from the shelf lattice, which holds only the samples the ground is
        /// meant to rise to — see <see cref="carriesShelf"/>. A bridge deck is therefore invisible here
        /// in both the average and the distance, which is what leaves the valley under it.</para>
        /// </summary>
        private float RoadFieldAt(float x, float z, out float nearestDistance)
        {
            float weightSum = 0f;
            float valueSum = 0f;
            float nearestSqr = float.MaxValue;

            int minColumn = Mathf.Max(0, ColumnOf(x - InfluenceRadius));
            int maxColumn = Mathf.Min(columns - 1, ColumnOf(x + InfluenceRadius));
            int minRow = Mathf.Max(0, RowOf(z - InfluenceRadius));
            int maxRow = Mathf.Min(rows - 1, RowOf(z + InfluenceRadius));

            float influenceSqr = InfluenceRadius * InfluenceRadius;

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
                        float distanceSqr = dx * dx + dz * dz;

                        if (distanceSqr < nearestSqr)
                        {
                            nearestSqr = distanceSqr;
                        }

                        if (distanceSqr > influenceSqr)
                        {
                            continue;
                        }

                        // Inverse fifth power. The exponent decides how far a neighbouring switchback leg
                        // reaches across: sharp keeps the ground next to each carriageway flat, while two legs
                        // at equal distance still average, so the retaining wall between them survives.
                        float distance = Mathf.Sqrt(distanceSqr);
                        float weight = 1f / (distanceSqr * distanceSqr * distance + 0.001f);

                        weightSum += weight;
                        valueSum += weight * samples[index].y;
                    }
                }
            }

            if (weightSum <= 0f)
            {
                int nearest = NearestInLattice(cellStart, cellItems, x, z, out nearestSqr);
                nearestDistance = Mathf.Sqrt(nearestSqr);
                return nearest >= 0 ? samples[nearest].y : 0f;
            }

            nearestDistance = Mathf.Sqrt(nearestSqr);
            return valueSum / weightSum;
        }

        /// <summary>
        /// Sorts the samples into a uniform grid, stored as counts turned into offsets. Without this the
        /// height lookup is every grid point against every road sample — about 24 million distance checks for
        /// a pass this size, and it gets worse as the world grows.
        /// </summary>
        /// <param name="include">
        /// Optional mask over the first <paramref name="sampleCount"/> samples. Null takes all of them.
        /// It exists because the shelf lattice is no longer a plain prefix of the array: a bridge sample
        /// is road for <see cref="DistanceToRoad"/> and not road for the shelf, and the two sets are
        /// interleaved rather than nested.
        /// </param>
        private void BuildBuckets(int sampleCount, bool[] include, out int[] starts, out int[] items)
        {
            int cellCount = columns * rows;
            var counts = new int[cellCount + 1];
            int included = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                if (include != null && !include[i])
                {
                    continue;
                }

                counts[CellOf(samples[i].x, samples[i].z) + 1]++;
                included++;
            }

            for (int cell = 0; cell < cellCount; cell++)
            {
                counts[cell + 1] += counts[cell];
            }

            starts = counts;
            items = new int[included];
            var cursor = new int[cellCount];

            for (int i = 0; i < sampleCount; i++)
            {
                if (include != null && !include[i])
                {
                    continue;
                }

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
            return Mathf.FloorToInt((x - gridOrigin.x) / BucketSize);
        }

        private int RowOf(float z)
        {
            return Mathf.FloorToInt((z - gridOrigin.y) / BucketSize);
        }
    }
}
