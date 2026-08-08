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

        private readonly int[] cellStart;
        private readonly int[] cellItems;
        private readonly Vector2 gridOrigin;
        private readonly int columns;
        private readonly int rows;

        private readonly float[] coarseHeights;
        private readonly Vector2 coarseOrigin;
        private readonly int coarseColumns;
        private readonly int coarseRows;

        public MountainField(IRoadPath path, in TerrainShape terrainShape, float sampleSpacing = 4f)
        {
            shape = terrainShape;

            float length = path.Length;
            int count = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(1f, sampleSpacing)) + 1);

            samples = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                samples[i] = path.GetPositionAtDistance(length * i / (count - 1));
            }

            // The grid has to be described before the buckets can be filled, because filling them uses
            // CellOf — so these are assigned first and read from there.
            Bounds bounds = RoadBounds;
            gridOrigin = new Vector2(bounds.min.x - InfluenceRadius, bounds.min.z - InfluenceRadius);
            columns = Mathf.Max(1, Mathf.CeilToInt((bounds.size.x + InfluenceRadius * 2f) / BucketSize));
            rows = Mathf.Max(1, Mathf.CeilToInt((bounds.size.z + InfluenceRadius * 2f) / BucketSize));

            BuildBuckets(out cellStart, out cellItems);

            coarseOrigin = new Vector2(bounds.min.x - CoarseMargin, bounds.min.z - CoarseMargin);
            coarseColumns = Mathf.Max(2, Mathf.CeilToInt((bounds.size.x + CoarseMargin * 2f) / CoarseCellSize) + 1);
            coarseRows = Mathf.Max(2, Mathf.CeilToInt((bounds.size.z + CoarseMargin * 2f) / CoarseCellSize) + 1);
            coarseHeights = BuildCoarseField();
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
            if (away <= 0f)
            {
                return shelf;
            }

            float carve = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(away / Mathf.Max(1f, shape.BlendDistance)));
            return Mathf.Lerp(shelf, MountainAt(x, z), carve);
        }

        /// <summary>Plan distance from the nearest point of road, metres.</summary>
        public float DistanceToRoad(float x, float z)
        {
            RoadFieldAt(x, z, out float nearest);
            return nearest;
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
                    float nearestSqr = float.MaxValue;
                    int nearest = 0;

                    for (int i = 0; i < samples.Length; i++)
                    {
                        float dx = samples[i].x - x;
                        float dz = samples[i].z - z;
                        float distanceSqr = dx * dx + dz * dz;

                        if (distanceSqr < nearestSqr)
                        {
                            nearestSqr = distanceSqr;
                            nearest = i;
                        }

                        if (distanceSqr > reachSqr)
                        {
                            continue;
                        }

                        // The constant in the denominator is what keeps this gentle: without it the nearest
                        // sample would dominate and the mountain would follow every leg.
                        float weight = 1f / (Mathf.Sqrt(distanceSqr) + 40f);
                        weightSum += weight;
                        valueSum += weight * samples[i].y;
                    }

                    heights[row * coarseColumns + column] =
                        weightSum > 0f ? valueSum / weightSum : samples[nearest].y;
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
                int nearest = NearestByBruteForce(x, z, out nearestSqr);
                nearestDistance = Mathf.Sqrt(nearestSqr);
                return samples[nearest].y;
            }

            nearestDistance = Mathf.Sqrt(nearestSqr);
            return valueSum / weightSum;
        }

        private int NearestByBruteForce(float x, float z, out float nearestSqr)
        {
            nearestSqr = float.MaxValue;
            int nearest = 0;

            for (int i = 0; i < samples.Length; i++)
            {
                float dx = samples[i].x - x;
                float dz = samples[i].z - z;
                float distanceSqr = dx * dx + dz * dz;
                if (distanceSqr < nearestSqr)
                {
                    nearestSqr = distanceSqr;
                    nearest = i;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Sorts the samples into a uniform grid, stored as counts turned into offsets. Without this the
        /// height lookup is every grid point against every road sample — about 24 million distance checks for
        /// a pass this size, and it gets worse as the world grows.
        /// </summary>
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
            return Mathf.FloorToInt((x - gridOrigin.x) / BucketSize);
        }

        private int RowOf(float z)
        {
            return Mathf.FloorToInt((z - gridOrigin.y) / BucketSize);
        }
    }
}
