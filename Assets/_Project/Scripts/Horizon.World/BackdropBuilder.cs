using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Builds the ring of distant mountain silhouettes that stands behind the whole world.
    ///
    /// <para><b>The world has no background, and that is structural rather than an omission.</b>
    /// <see cref="TerrainShape.CorridorWidth"/> is 200 m: ground exists only that far from a road, so
    /// the entire world is a ribbon of hillside hanging in an empty plane — which is exactly what
    /// <c>WorldPreview_Overview</c> is a picture of. Fog hides the edge from the car, so nobody ever
    /// sees the corridor stop. What they see instead is that no straight in this game ends in anything:
    /// the road runs into a flat wall of fog colour and there is never a distance behind it.</para>
    ///
    /// <para>This is the cheapest possible answer and it does not touch the world at all. Three
    /// concentric curtains of faceted ridge, 64 segments each, one draw call, 384 triangles for every
    /// view from every road in the game. No collider, no streaming, no chunk, no terrain tile, no
    /// height field — <see cref="MountainField"/> never hears about it, so there is no risk of the
    /// class of fault this project keeps paying for, where a thing placed near a road quietly changes
    /// the ground under the road.</para>
    ///
    /// <para><b>The profile is a sum of integer harmonics, which is the whole reason there is no
    /// seam.</b> A ridge line drawn round a circle has to close on itself exactly. Sampling any noise
    /// field along that circle does not close — it leaves one vertical crack at a single bearing, which
    /// reads as a rendering fault rather than as a texture one and is invisible from every other
    /// direction. <c>cos(k * theta)</c> for whole-number <c>k</c> is periodic over the circle by
    /// construction, so the profile meets itself to the last bit. It is the same argument the sky's
    /// cloud field makes for measuring its own wrap, arrived at from the other side: here the wrap can
    /// be had for free, so it is.</para>
    ///
    /// <para>Sixty-four segments and not two hundred, deliberately. At 5.6 degrees a segment the ridge
    /// line is visibly faceted, which is the idiom of everything else in this world; smooth it and the
    /// backdrop is the one object in the frame that does not look like it belongs to the same game.</para>
    /// </summary>
    public static class BackdropBuilder
    {
        /// <summary>How many silhouettes stand behind one another.</summary>
        public const int Rings = 3;

        /// <summary>Facets round one ring.</summary>
        private const int Segments = 64;

        /// <summary>
        /// Radius of the nearest ring, metres, and the step out to each one behind it.
        ///
        /// <para><b>These are constrained from both sides and there is not much room between.</b>
        /// Nearer than about six hundred metres and the rings start occluding real hillside that the
        /// fog has not finished taking; further out and the curtain's own hem leaves the far plane and
        /// is clipped along a hard horizontal line. What a mountain's distance actually reads as is its
        /// angular size, not its range, so there is nothing to gain by pushing them out — a 190 m
        /// summit at 640 m stands sixteen degrees high, which is a range rather than a hill.</para>
        /// </summary>
        private const float NearRadius = 640f;

        private const float RadiusStep = 60f;

        /// <summary>
        /// The far plane every camera in this project has to carry for the backdrop to survive it.
        ///
        /// <para>Read by the game camera and by every preview renderer rather than each of them
        /// carrying 600, because a camera that clips this draws a hard horizontal edge across the
        /// bottom of the ring and nothing anywhere says why. It is the slant range to the furthest
        /// corner of the outermost curtain, with a little over it.</para>
        ///
        /// <para>It costs nothing to raise. There is no geometry in this world beyond about three
        /// hundred metres from a road — <c>TerrainShape.CorridorWidth</c> is 200 — so a longer plane
        /// draws no more of anything; it only asks the depth buffer for a range it has ample precision
        /// for at a 0.3 m near plane.</para>
        /// </summary>
        public static float MinimumFarPlane
        {
            get
            {
                // Both extremes, because the ring reaches furthest at whichever of its two edges is
                // taller — the hem below or a summit above. Taking only the hem was right until the
                // peaks were raised, which is exactly the shape of a number that goes quietly wrong.
                float extreme = Mathf.Max(HemDrop, MaximumRise - BaseDrop);
                float slant = Mathf.Sqrt(OuterRadius * OuterRadius + extreme * extreme);

                return Mathf.Ceil(slant / 50f) * 50f + 50f;
            }
        }

        /// <summary>Radius of the outermost ring.</summary>
        private static float OuterRadius => NearRadius + (Rings - 1) * RadiusStep;

        /// <summary>
        /// Where a ridge stands relative to the camera rig, and how far its curtain hangs below that.
        ///
        /// <para>The hem is a long way down and it is not decoration. Over the strait, or looking out
        /// from the col, there is no terrain at all below the horizon for the backdrop to hide behind,
        /// so a curtain that simply stopped would draw a hard horizontal line across open water. It
        /// hangs to <see cref="HemDrop"/> and fades into the air on the way down, which is what the
        /// shader's <c>_Emergence</c> does with the height this builder bakes.</para>
        /// </summary>
        private const float BaseDrop = 40f;

        private const float HemDrop = 260f;

        /// <summary>
        /// Lowest and highest a ridge rises above <see cref="BaseDrop"/>, metres.
        ///
        /// <para><b>The first pair were 26 and 190 and they were too low, which only one kind of frame
        /// said so.</b> Looking along a valley the ring was right — the ranges behind Seeburg's water
        /// read as a far shore at the first try. On the open flat of Anadolu the terrain fills the
        /// frame to the skyline and occludes everything but the summits, and at 190 a typical summit
        /// stood three degrees over the horizon, which is a bump. The profile's own mean is what does
        /// this: three cosines average a half and the sharpening power takes that to a third, so the
        /// ceiling is reached almost nowhere and the number that matters is the typical rise rather
        /// than the maximum.</para>
        /// </summary>
        private const float MinimumRise = 34f;

        private const float MaximumRise = 240f;

        /// <summary>
        /// The three harmonics a ridge is made of, and what each contributes.
        ///
        /// <para>Coprime and spread wide: 3 gives the range its massifs, 11 the individual mountains and
        /// 29 the notches between them. Share a factor and the small shapes line up with the large ones
        /// at every repeat, and a range with a rhythm in it reads as wallpaper.</para>
        /// </summary>
        private static readonly int[] Harmonics = { 3, 11, 29 };

        private static readonly float[] Weights = { 0.54f, 0.31f, 0.15f };

        /// <summary>
        /// Builds every ring into one mesh.
        /// </summary>
        /// <param name="meshName">Asset name for the mesh.</param>
        /// <param name="triangleCount">How many triangles came out, for the build log.</param>
        public static Mesh Build(string meshName, out int triangleCount)
        {
            int corners = Segments + 1;

            var vertices = new Vector3[Rings * corners * 2];
            var colours = new Color32[vertices.Length];
            var triangles = new int[Rings * Segments * 6];

            int vertex = 0;
            int index = 0;

            // Far ring first. Depth sorts them now that the pass writes it, so this is no longer load
            // bearing — it is kept because the vertex order matching the visual order is what makes the
            // mesh readable in a debugger, and because it costs a loop counter.
            for (int ring = Rings - 1; ring >= 0; ring--)
            {
                float depth = Rings > 1 ? ring / (float)(Rings - 1) : 0f;
                float radius = NearRadius + ring * RadiusStep;

                // A ring further out is drawn smaller as well as hazier. Both are how distance reads,
                // and having only the colour do it leaves three ranges of identical stature standing in
                // a row, which is a stage set rather than a country.
                float reach = Mathf.Lerp(MaximumRise, MaximumRise * 0.62f, depth);

                int ringStart = vertex;

                for (int segment = 0; segment < corners; segment++)
                {
                    // The last corner is the first one again, by construction rather than by wrapping an
                    // index: sharing the vertex would be one fewer, and duplicating it costs nothing and
                    // cannot go wrong.
                    float angle = segment / (float)Segments * Mathf.PI * 2f;

                    float rise = Mathf.Lerp(MinimumRise, reach, Profile(angle, ring));

                    float x = Mathf.Sin(angle) * radius;
                    float z = Mathf.Cos(angle) * radius;

                    vertices[vertex] = new Vector3(x, -HemDrop, z);
                    vertices[vertex + 1] = new Vector3(x, -BaseDrop + rise, z);

                    // r is the ring, g is how tall this vertex stands against the tallest summit the
                    // backdrop can produce. Measured against a constant rather than against this ring's
                    // own maximum, so a low far range stays in the haze instead of being lightened as
                    // though it were a peak.
                    float top = Mathf.Clamp01((rise + HemDrop - BaseDrop) / (MaximumRise + HemDrop - BaseDrop));

                    colours[vertex] = Encode(depth, 0f);
                    colours[vertex + 1] = Encode(depth, top);

                    vertex += 2;
                }

                for (int segment = 0; segment < Segments; segment++)
                {
                    int hem = ringStart + segment * 2;
                    int crest = hem + 1;
                    int nextHem = hem + 2;
                    int nextCrest = hem + 3;

                    triangles[index++] = hem;
                    triangles[index++] = crest;
                    triangles[index++] = nextHem;

                    triangles[index++] = nextHem;
                    triangles[index++] = crest;
                    triangles[index++] = nextCrest;
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.SetVertices(vertices);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);

            // The bounds are set rather than recalculated, and they are enormous on purpose. This object
            // is moved to the camera every frame by Backdrop, and a mesh whose bounds are honest gets
            // culled the instant the rig looks away from the centre of a ring it is standing inside.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(4000f, 4000f, 4000f));

            triangleCount = triangles.Length / 3;
            return mesh;
        }

        /// <summary>
        /// How high the ridge stands at this bearing, 0 to 1.
        ///
        /// <para>Each harmonic is offset by a quarter turn per ring, so the three silhouettes never put
        /// their summits at the same bearing — which is what would make them read as one mountain drawn
        /// three times rather than as a range behind a range.</para>
        ///
        /// <para>Raised to a power at the end because a sum of cosines is a landscape of rolling hills
        /// and this wants summits: the same profile with the low ground pushed lower reads as rock
        /// instead of as moor.</para>
        /// </summary>
        private static float Profile(float angle, int ring)
        {
            float sum = 0f;

            for (int i = 0; i < Harmonics.Length; i++)
            {
                float phase = (ring + 1) * (i + 1) * 1.618f;
                sum += Weights[i] * (0.5f + 0.5f * Mathf.Cos(Harmonics[i] * angle + phase));
            }

            // 1.5 and not 1.7. The exponent is what separates summits from moorland, and it also
            // decides how much of the ceiling the range ever reaches — at 1.7 the average ridge stood
            // at under a third of MaximumRise, so raising the ceiling alone would have sharpened a few
            // peaks and left the skyline as flat as it was.
            return Mathf.Pow(Mathf.Clamp01(sum), 1.5f);
        }

        /// <summary>Packs the two blend factors the shader reads into a vertex colour.</summary>
        private static Color32 Encode(float depth, float height)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Clamp01(depth) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(height) * 255f),
                0,
                255);
        }
    }
}
