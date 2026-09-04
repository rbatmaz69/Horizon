using UnityEngine;

namespace Horizon.Core
{
    /// <summary>
    /// The road is not flat. A world-space height field, sampled at a wheel's contact point.
    ///
    /// <para><b>The carriageway meshes are geometrically perfect and this does not change one of
    /// them.</b> The terrain has <c>TerrainShape.DetailAmplitude</c>; the asphalt has nothing, so a
    /// suspension with four raycasts, a spring, a damper, two anti-roll bars and a load-dependent grip
    /// curve had nothing whatever to work against until the car reached a verge. The whole model stood
    /// still on the one surface the game is played on.</para>
    ///
    /// <para>Displacing the mesh was the other way to do it and it is worse in three separate ways: it
    /// costs vertices on the heaviest tiles in the world, it breaks every piece of laid-on paving this
    /// project has already paid to get flush (start lines, grid boxes, fork throats, forecourt aisles —
    /// all of which only sit flat where the surface under them has no camber to follow), and it would
    /// put bumps in the shadow map. Offsetting what the *wheel* is told instead costs no triangle, no
    /// draw call and no asset.</para>
    ///
    /// <para><b>In Horizon.Core beside <see cref="GroundSurface"/>, and for the same reason that one
    /// gives.</b> This is knowledge about the world, not about the car: <c>Horizon.Vehicle</c> reads it
    /// and may not see <c>Horizon.World</c>, and <c>Horizon.EditorTools</c> reads it to check it. Core is
    /// the only assembly both can reach. It is a pure function of a position — no state, no seed, no
    /// time — so a parked car is perfectly still, which a time-varying field would not give.</para>
    ///
    /// <para><b>The noise is hand-rolled rather than <c>Mathf.PerlinNoise</c>, and this is the one place
    /// in the project where that choice reverses.</b> <c>MountainField</c>, <c>TerrainTileBuilder</c> and
    /// <c>VegetationBuilder</c> all use Unity's and are right to: they bake a mesh once, so an
    /// implementation change would move the world and nothing else. Here the function is evaluated on the
    /// device at 50 Hz and its <i>derivative</i> is spent as a damper force, so what the fade does at a
    /// lattice line is load-bearing. A quintic fade is C2, which means the shaft acceleration is
    /// continuous; a cubic one is not, and the damper would read its second-derivative step as a knock.
    /// </para>
    /// </summary>
    public static class SurfaceRelief
    {
        /// <summary>
        /// The long swell, in metres. Broad enough that the body rides it rather than the tyre.
        ///
        /// <para>Not scaled by the surface gain, deliberately. A swell this long belongs to the ground
        /// rather than to what is painted on it, and leaving it out of the gain halves the step the field
        /// takes at every asphalt-to-verge boundary.</para>
        /// </summary>
        public const float LongWavelength = 31f;

        public const float LongAmplitude = 0.0030f;

        /// <summary>The middle octave: felt through the seat rather than seen.</summary>
        public const float MidWavelength = 11f;

        public const float MidAmplitude = 0.0010f;

        /// <summary>
        /// The fine octave, and its wavelength is chosen against the car rather than against taste.
        ///
        /// <para>All ten cars share one wheelbase (3.375 m) and one track (2.475 m). Value noise
        /// decorrelates in about a lattice cell, so an octave within roughly half a factor of either
        /// spacing — or of twice it — locks the wheels into a fixed pattern and the car sits in a
        /// standing wave, heaving or pitching in place instead of being unsettled. 4 m would have been
        /// the load budget's answer and it sits between the wheelbase and twice the track. 5.8 m clears
        /// twice the track by 17 % and stays under twice the wheelbase by 14 %, which is the widest gap
        /// available above the sampling floor: at 50 Hz and top speed the car advances 1.2 m a step, so
        /// anything under about 2.5 m is being aliased rather than driven over.</para>
        /// </summary>
        public const float ShortWavelength = 5.8f;

        public const float ShortAmplitude = 0.00076f;

        /// <summary>
        /// The most the field can ever lift the road, in metres — all three octaves in phase at the
        /// loudest surface.
        ///
        /// <para>Published because the wheel's ray has to be long enough to find a road it is about to be
        /// told is higher than it is. Reading this rather than adding a number there means widening the
        /// field moves the ray with it.</para>
        /// </summary>
        public const float PeakHeight = (LongAmplitude + (MidAmplitude + ShortAmplitude) * LoudestGain);

        /// <summary>The largest <see cref="GainOf"/> returns, so <see cref="PeakHeight"/> is a real bound.</summary>
        private const float LoudestGain = 3.4f;

        /// <summary>
        /// How loud the two short octaves are on a given surface.
        ///
        /// <para>The verge and the open ground being unsettling is the point rather than a side effect:
        /// the driver already loses grip there and already hears it, and now the car moves there too.
        /// Asphalt is 1 so that the number the amplitudes were sized against is the one the road uses.
        /// </para>
        /// </summary>
        public static float GainOf(SurfaceKind value)
        {
            switch (value)
            {
                case SurfaceKind.Shoulder:
                    return 2.6f;
                case SurfaceKind.Ground:
                    return LoudestGain;
                default:
                    return 1f;
            }
        }

        /// <summary>
        /// The road's height at a world position, in metres, signed about zero.
        /// </summary>
        /// <param name="shortGain">
        /// Scales the two short octaves — <see cref="GainOf"/>, but eased by the caller rather than read
        /// raw. <c>GainOf</c> is a step function in space, and a step in a height field is a step in the
        /// distance a wheel measures, which is precisely the kerb <c>VehicleController</c>'s damper clamp
        /// exists to survive. Eased, it is a crossfade with a stated time constant instead.
        /// </param>
        public static float HeightAt(float x, float z, float shortGain)
        {
            // Each octave on its own rotated lattice. Value noise puts its extrema on lattice points, so
            // three octaves sharing an orientation would line their peaks up along the world axes and a
            // road running due east would meet a bump at exactly one wavelength, every time.
            float relief = LongAmplitude * Octave(x, z, 1f / LongWavelength, 0.9239f, 0.3827f, 0);

            float fine = MidAmplitude * Octave(x, z, 1f / MidWavelength, 0.5878f, 0.8090f, 1013)
                       + ShortAmplitude * Octave(x, z, 1f / ShortWavelength, -0.2588f, 0.9659f, 7919);

            return relief + fine * shortGain;
        }

        /// <summary>One octave of rotated value noise, returned in -1..1.</summary>
        private static float Octave(
            float x, float z, float inverseWavelength, float cos, float sin, int seed)
        {
            float u = (x * cos - z * sin) * inverseWavelength;
            float v = (x * sin + z * cos) * inverseWavelength;

            // FloorToInt rather than a cast: (int) truncates towards zero, which would mirror the whole
            // field about the origin and put a seam down the middle of the world.
            int xi = Mathf.FloorToInt(u);
            int zi = Mathf.FloorToInt(v);

            float fx = u - xi;
            float fz = v - zi;

            // 6t^5 - 15t^4 + 10t^3. Quintic because it is C2 — see the class remarks.
            float sx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
            float sz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);

            float a = Mathf.Lerp(Hash(xi, zi, seed), Hash(xi + 1, zi, seed), sx);
            float b = Mathf.Lerp(Hash(xi, zi + 1, seed), Hash(xi + 1, zi + 1, seed), sx);

            return Mathf.Lerp(a, b, sz) * 2f - 1f;
        }

        /// <summary>An integer hash to 0..1. Allocation-free and identical on every platform.</summary>
        private static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)z * 668265263u + (uint)seed;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return h * (1f / 4294967295f);
            }
        }
    }
}
