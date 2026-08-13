using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Lays the water surface over whatever <see cref="MountainField.UnderWater"/> has dug out,
    /// one piece per terrain tile.
    ///
    /// <para><b>Per tile, so streaming comes free.</b> The mesh is hung under the terrain tile it
    /// belongs to before that tile's <c>WorldChunk</c> is added, exactly the way the vegetation and
    /// the town buildings are, so it inherits the chunk's bounds, its load radius and its asset
    /// naming without any of them being mentioned here. Water then exists precisely where terrain
    /// exists, which is the only place anybody can see it from.</para>
    ///
    /// <para><b>No water shader, and none wanted.</b> <c>Horizon/VertexTintLit</c> multiplies its
    /// base colour by the vertex colour and already draws the terrain, the streets, the houses and
    /// the foliage. Depth is resolved here, at build time, and baked into the vertex colour — so the
    /// shading a real water shader would compute from a depth texture is simply already in the mesh.
    /// That matters because on Android this project has <b>no depth texture and no opaque
    /// texture</b>: a transparent shader there would get no soft shore, no refraction and, with
    /// <c>m_ShadowTransparentReceive: 0</c>, no shadows either. It also matches what the game already
    /// does everywhere else — the car's glass is opaque, and the fountain in the market square is an
    /// unlit disc. The cost is that the water does not move.</para>
    ///
    /// <para><b>The edge dives to meet the ground.</b> A flat plane clipped to the waterline leaves a
    /// hairline of terrain showing through wherever the two disagree, and the two do disagree: the
    /// terrain mesh is a linear interpolation across 12 m cells while the height field is smooth, so
    /// they differ by a metre or two on a slope. Instead every vertex takes
    /// <c>max(surface, groundOfTheMesh)</c> — level inside the water, and rising out of it to sit
    /// exactly on the terrain at the bank. The ground is sampled through
    /// <see cref="TerrainTileBuilder.SampleSurface"/> rather than <c>HeightAt</c> for that reason:
    /// the question is where the mesh is, not where the field says it is.</para>
    /// </summary>
    public static class WaterTileBuilder
    {
        /// <summary>Colour of water a hand deep. Warm, because it is the sand showing through.</summary>
        private static readonly Color32 ShallowTint = new Color(0.42f, 0.58f, 0.55f);

        /// <summary>And of water deep enough to hide its bed.</summary>
        private static readonly Color32 DeepTint = new Color(0.10f, 0.20f, 0.30f);

        /// <summary>The band of broken water along the shore.</summary>
        private static readonly Color32 FoamTint = new Color(0.78f, 0.82f, 0.82f);

        /// <summary>
        /// Over how many metres of depth the colour reaches its deep value.
        ///
        /// <para>3.5, not 6, and the difference is the whole look. The rivers here are 3.5 m deep at
        /// the middle, so a ramp that only bottomed out at 6 m never got past its own halfway point:
        /// every body in the world came out the pale grey-green of a puddle. It has to be set near the
        /// depth of the shallowest body that matters, not at some depth water might reach.</para>
        /// </summary>
        private const float DeepAt = 3.5f;

        /// <summary>Under how much water the foam band shows.</summary>
        private const float FoamDepth = 0.55f;

        /// <summary>
        /// Builds one tile's worth of water, or null if no body reaches it.
        /// </summary>
        public static Mesh BuildTile(
            TerrainTileKey key,
            MountainField field,
            in TerrainShape shape,
            IReadOnlyList<WaterBody> waters,
            string meshName,
            out int triangleCount)
        {
            triangleCount = 0;

            if (waters == null || waters.Count == 0)
            {
                return null;
            }

            float tileSize = TerrainTileBuilder.TileSize(shape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            float cell = shape.CellSize;
            int cells = Mathf.Max(1, Mathf.RoundToInt(tileSize / cell));

            var vertices = new List<Vector3>(256);
            var colours = new List<Color32>(256);
            var triangles = new List<int>(384);

            for (int w = 0; w < waters.Count; w++)
            {
                WaterBody body = waters[w];

                // The tile against the body's plan bounds first. Almost every pairing is a no, and the
                // rest of this loop is 225 height samples.
                if (originX + tileSize < body.Plan.min.x || originX > body.Plan.max.x
                    || originZ + tileSize < body.Plan.min.z || originZ > body.Plan.max.z)
                {
                    continue;
                }

                AppendBody(body, field, shape, originX, originZ, cell, cells,
                    vertices, colours, triangles);
            }

            if (triangles.Count == 0)
            {
                return null;
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.SetVertices(vertices);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            triangleCount = triangles.Count / 3;
            return mesh;
        }

        /// <summary>
        /// One body's surface across one tile.
        ///
        /// <para>A quad is emitted when <i>any</i> of its four corners is under open water, not when
        /// all four are. Emitting only the fully covered ones stops the surface a whole cell short of
        /// its own shore and leaves a ring of dug basin showing all the way round; the corners that
        /// are outside simply rise to sit on the terrain, which is what closes the waterline.</para>
        /// </summary>
        private static void AppendBody(
            in WaterBody body,
            MountainField field,
            in TerrainShape shape,
            float originX,
            float originZ,
            float cell,
            int cells,
            List<Vector3> vertices,
            List<Color32> colours,
            List<int> triangles)
        {
            int corners = cells + 1;

            var height = new float[corners * corners];
            var depth = new float[corners * corners];
            var wet = new bool[corners * corners];

            for (int row = 0; row < corners; row++)
            {
                for (int column = 0; column < corners; column++)
                {
                    float x = originX + column * cell;
                    float z = originZ + row * cell;
                    int index = row * corners + column;

                    TerrainTileBuilder.SampleSurface(field, shape, x, z,
                        out Vector3 ground, out Vector3 _);

                    bool inside = body.DistanceOutside(x, z) <= 0f;

                    wet[index] = inside && ground.y < body.SurfaceY;
                    depth[index] = Mathf.Max(0f, body.SurfaceY - ground.y);

                    // Level over the water, and climbing out onto the bank so the two meshes meet.
                    height[index] = Mathf.Max(body.SurfaceY, ground.y);
                }
            }

            for (int row = 0; row < cells; row++)
            {
                for (int column = 0; column < cells; column++)
                {
                    int i00 = row * corners + column;
                    int i10 = i00 + 1;
                    int i01 = i00 + corners;
                    int i11 = i01 + 1;

                    if (!wet[i00] && !wet[i10] && !wet[i01] && !wet[i11])
                    {
                        continue;
                    }

                    float x0 = originX + column * cell;
                    float z0 = originZ + row * cell;

                    int start = vertices.Count;

                    Add(vertices, colours, x0, height[i00], z0, depth[i00]);
                    Add(vertices, colours, x0 + cell, height[i10], z0, depth[i10]);
                    Add(vertices, colours, x0, height[i01], z0 + cell, depth[i01]);
                    Add(vertices, colours, x0 + cell, height[i11], z0 + cell, depth[i11]);

                    // Wound so the face points up, which is the only way anybody looks at water here.
                    triangles.Add(start);
                    triangles.Add(start + 2);
                    triangles.Add(start + 1);

                    triangles.Add(start + 1);
                    triangles.Add(start + 2);
                    triangles.Add(start + 3);
                }
            }
        }

        private static void Add(
            List<Vector3> vertices, List<Color32> colours, float x, float y, float z, float depth)
        {
            vertices.Add(new Vector3(x, y, z));
            colours.Add(TintFor(depth));
        }

        /// <summary>
        /// The colour of water this deep.
        ///
        /// <para>Foam first and only in the last half-metre, so the shore reads as a line rather than
        /// as a gradient that happens to end. Everything past that is the shallow-to-deep ramp, eased
        /// rather than linear because the interesting part of the change is all in the first two
        /// metres.</para>
        /// </summary>
        private static Color32 TintFor(float depth)
        {
            if (depth < FoamDepth)
            {
                return Color32.Lerp(FoamTint, ShallowTint, Mathf.Clamp01(depth / FoamDepth));
            }

            float t = Mathf.Clamp01((depth - FoamDepth) / Mathf.Max(0.01f, DeepAt - FoamDepth));
            return Color32.Lerp(ShallowTint, DeepTint, Mathf.SmoothStep(0f, 1f, t));
        }
    }
}
