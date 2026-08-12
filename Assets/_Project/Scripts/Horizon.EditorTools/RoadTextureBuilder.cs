using Horizon.World;
using UnityEngine;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Generates the road surface and shoulder textures. Procedural, like everything else in this
    /// project — no art files, and the markings stay correct when the road width changes because they
    /// are painted from the same numbers the mesh is built from.
    ///
    /// The surface texture holds **two variants side by side** along u: the left half has a dashed
    /// centre line, the right half a solid one. The mesh picks a half per cross-section, which is how
    /// the centre line goes solid through a hairpin without a second material or extra geometry.
    /// </summary>
    public static class RoadTextureBuilder
    {
        /// <summary>Pixels across one variant, i.e. across the asphalt.</summary>
        private const int VariantPixels = 512;

        /// <summary>Pixels along one marking cycle.</summary>
        private const int LengthPixels = 512;

        private static readonly Color AsphaltBase = new Color(0.200f, 0.195f, 0.205f);
        private static readonly Color PaintBase = new Color(0.86f, 0.86f, 0.83f);

        /// <summary>u range of the dashed variant.</summary>
        public const float DashedVariantStart = 0f;

        /// <summary>u range of the solid variant.</summary>
        public const float SolidVariantStart = 0.5f;

        /// <summary>Width in u of either variant.</summary>
        public const float VariantWidth = 0.5f;

        /// <summary>Builds the two-variant road surface texture.</summary>
        /// <param name="laneCount">
        /// How many lanes the asphalt carries, which decides how many lines are painted between its
        /// edges — <c>laneCount - 1</c> of them, evenly spaced.
        ///
        /// <para>Two is the two-way road this started as, where the single interior line is the centre
        /// line and goes solid through a corner. Four is one carriageway of the motorway, where the three
        /// interior lines are lane dividers and are always dashed: a one-way carriageway has no
        /// overtaking prohibition to express, so the solid variant is painted identically and
        /// <c>RoadShape.Autobahn</c> keeps the mesh off it anyway.</para>
        /// </param>
        public static Texture2D BuildSurface(in RoadShape shape, int laneCount = 2)
        {
            int width = VariantPixels * 2;
            var texture = new Texture2D(width, LengthPixels, TextureFormat.RGBA32, true);

            float asphaltWidth = shape.HalfWidth * 2f;
            float metresPerPixelAcross = asphaltWidth / VariantPixels;
            float cycle = Mathf.Max(0.5f, shape.Markings.CycleLength);
            float metresPerPixelAlong = cycle / LengthPixels;

            // Soften the paint edges by about a pixel so the lines are not jagged.
            float acrossSoftness = metresPerPixelAcross * 1.2f;
            float alongSoftness = metresPerPixelAlong * 1.2f;

            var pixels = new Color[width * LengthPixels];

            for (int y = 0; y < LengthPixels; y++)
            {
                float alongFraction = y / (float)LengthPixels;
                float alongMetres = alongFraction * cycle;

                for (int x = 0; x < width; x++)
                {
                    bool solidVariant = x >= VariantPixels;
                    int variantX = solidVariant ? x - VariantPixels : x;

                    // Position across the carriageway, metres, zero at the centre line.
                    float across = (variantX / (float)(VariantPixels - 1) - 0.5f) * asphaltWidth;

                    Color colour = SampleAsphalt(across, alongFraction, shape);
                    float paint = PaintCoverage(
                        across, alongMetres, shape, solidVariant, acrossSoftness, alongSoftness, laneCount);

                    if (paint > 0f)
                    {
                        // Paint is worn, not showroom-fresh.
                        float wear = 0.82f + 0.18f * Noise(across * 3.1f, alongFraction, 7f, 31.7f);
                        colour = Color.Lerp(colour, PaintBase * wear, paint);
                    }

                    pixels[y * width + x] = colour;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>Builds the gravel shoulder texture. Tiles in both directions.</summary>
        public static Texture2D BuildShoulder(int size = 256)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true);
            var pixels = new Color[size * size];

            var baseColour = new Color(0.42f, 0.39f, 0.345f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Two lattice frequencies, both wrapping on the texture, so it tiles exactly.
                    float coarse = PeriodicValueNoise(x, y, size, 16, 11);
                    float fine = PeriodicValueNoise(x, y, size, 64, 29);
                    float speckle = PeriodicValueNoise(x, y, size, 128, 53);

                    float shade = 0.82f + 0.22f * coarse + 0.16f * (fine - 0.5f) + 0.12f * (speckle - 0.5f);
                    pixels[y * size + x] = baseColour * shade;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Color SampleAsphalt(float across, float alongFraction, in RoadShape shape)
        {
            float grain = Noise(across * 2.4f, alongFraction, 5f, 3.3f) - 0.5f;
            float coarse = Noise(across * 0.7f, alongFraction, 2f, 17.1f) - 0.5f;

            float shade = 1f + grain * 0.14f + coarse * 0.10f;

            // Wheel tracks: slightly polished, slightly darker, one per lane centre.
            float laneCentre = shape.HalfWidth * 0.5f;
            float track = Mathf.Exp(-Mathf.Pow((Mathf.Abs(across) - laneCentre) / 0.55f, 2f));
            shade -= track * 0.09f;

            // The longitudinal joint where the two lanes were laid.
            float seam = Mathf.Exp(-Mathf.Pow(across / 0.07f, 2f));
            shade -= seam * 0.16f;

            return AsphaltBase * shade;
        }

        /// <summary>How much paint covers this point, 0 to 1, with soft edges.</summary>
        private static float PaintCoverage(
            float across,
            float alongMetres,
            in RoadShape shape,
            bool solidVariant,
            float acrossSoftness,
            float alongSoftness,
            int laneCount)
        {
            RoadMarkings markings = shape.Markings;

            // --- Edge lines. Inset from the asphalt edge, which doubles as the guard band that keeps
            // the wrap seam free of paint.
            float outer = shape.HalfWidth - markings.EdgeLineInset;
            float inner = outer - markings.EdgeLineWidth;
            float distanceFromCentre = Mathf.Abs(across);

            float coverage = Band(distanceFromCentre, inner, outer, acrossSoftness);

            // The dash is centred within the cycle rather than started at zero. Starting it at zero
            // would put its leading edge exactly on the texture's wrap seam, where it would be clipped
            // and half-faded.
            float dashHalf = markings.DashLength * 0.5f;
            float cycleMiddle = markings.CycleLength * 0.5f;
            float dash = Band(alongMetres, cycleMiddle - dashHalf, cycleMiddle + dashHalf, alongSoftness);

            // --- Interior lines: one fewer than there are lanes, evenly spaced across the asphalt.
            int lines = Mathf.Max(1, laneCount) - 1;
            float laneWidth = shape.HalfWidth * 2f / Mathf.Max(1, laneCount);

            for (int i = 1; i <= lines; i++)
            {
                float at = -shape.HalfWidth + i * laneWidth;

                float line = Inside(
                    Mathf.Abs(across - at), markings.CentreLineWidth * 0.5f, acrossSoftness);

                // A two-lane road's single interior line is a centre line, and the solid variant is what
                // says "do not overtake here". Everything else is a lane divider, which is dashed on
                // every road there is.
                bool alwaysDashed = laneCount != 2;
                if (alwaysDashed || !solidVariant)
                {
                    line *= dash;
                }

                coverage = Mathf.Max(coverage, line);
            }

            return Mathf.Clamp01(coverage);
        }

        /// <summary>Soft-edged membership of the range <paramref name="low"/>..<paramref name="high"/>.</summary>
        private static float Band(float value, float low, float high, float softness)
        {
            float rising = Mathf.InverseLerp(low - softness, low + softness, value);
            float falling = 1f - Mathf.InverseLerp(high - softness, high + softness, value);
            return Mathf.Clamp01(Mathf.Min(rising, falling));
        }

        /// <summary>Soft-edged test for "no further out than <paramref name="limit"/>".</summary>
        private static float Inside(float value, float limit, float softness)
        {
            return Mathf.Clamp01(1f - Mathf.InverseLerp(limit - softness, limit + softness, value));
        }

        /// <summary>
        /// Noise that is periodic along the road.
        ///
        /// The texture repeats every marking cycle, so noise sampled linearly along it would not match
        /// at the seam and every 12 m of road would show a line across the carriageway. Sampling Perlin
        /// around a circle makes the along-axis wrap onto itself exactly.
        /// </summary>
        private static float Noise(float across, float alongFraction, float frequency, float seed)
        {
            float angle = alongFraction * Mathf.PI * 2f;
            float x = Mathf.Cos(angle) * frequency + seed;
            float y = Mathf.Sin(angle) * frequency + seed;
            return Mathf.PerlinNoise(x + across, y);
        }

        /// <summary>Value noise on a wrapping lattice, so the result tiles on both axes.</summary>
        private static float PeriodicValueNoise(int x, int y, int size, int cells, int seed)
        {
            float cellSize = size / (float)cells;
            float fx = x / cellSize;
            float fy = y / cellSize;

            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = Mathf.SmoothStep(0f, 1f, fx - x0);
            float ty = Mathf.SmoothStep(0f, 1f, fy - y0);

            float v00 = Hash(x0, y0, cells, seed);
            float v10 = Hash(x0 + 1, y0, cells, seed);
            float v01 = Hash(x0, y0 + 1, cells, seed);
            float v11 = Hash(x0 + 1, y0 + 1, cells, seed);

            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), ty);
        }

        private static float Hash(int x, int y, int cells, int seed)
        {
            // Wrapping the lattice indices is what makes the noise tile.
            int wrappedX = ((x % cells) + cells) % cells;
            int wrappedY = ((y % cells) + cells) % cells;

            unchecked
            {
                uint h = (uint)(wrappedX * 73856093) ^ (uint)(wrappedY * 19349663) ^ (uint)(seed * 83492791);
                h ^= h << 13;
                h ^= h >> 17;
                h ^= h << 5;
                return (h & 0xFFFFFF) / (float)0xFFFFFF;
            }
        }
    }
}
