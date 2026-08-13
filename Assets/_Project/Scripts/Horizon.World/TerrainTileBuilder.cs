using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>Where a tile sits on the global lattice, and how big it is.</summary>
    public readonly struct TerrainTileKey
    {
        public readonly int Column;
        public readonly int Row;

        public TerrainTileKey(int column, int row)
        {
            Column = column;
            Row = row;
        }
    }

    /// <summary>
    /// Builds the terrain one tile at a time, and only where there is road to be near.
    ///
    /// The single-mesh terrain did not survive the pass growing: one mesh over the whole bounding box
    /// meant one draw call, one MeshCollider and tens of thousands of vertices that are all resident
    /// whether or not you can see them. A folded pass also leaves most of its bounding box empty, so
    /// generating only a corridor around the road throws away most of the work rather than most of the
    /// detail.
    /// </summary>
    public static class TerrainTileBuilder
    {
        /// <summary>Submesh for shallow ground, meant for a grass material.</summary>
        /// <summary>
        /// The two ground colours, moved here from the materials they used to be.
        ///
        /// <para>The numbers are the ones M_Grass and M_Rock carried, so the world comes out the colour
        /// it already was. Neither material was textured, so nothing is lost by folding them into the
        /// vertices — see <c>Horizon/VertexTintLit</c>, which is the shader the town's buildings already
        /// use for exactly this.</para>
        /// </summary>
        public static readonly Color32 GrassTint = new Color(0.36f, 0.48f, 0.26f);

        /// <summary>See <see cref="GrassTint"/>.</summary>
        public static readonly Color32 RockTint = new Color(0.44f, 0.39f, 0.34f);

        /// <summary>
        /// Ground near enough above water to be its shore.
        ///
        /// <para>Free, in the sense that matters: it is a third choice inside a comparison the tile
        /// builder was making anyway, so every beach in the world draws in the same call as the grass
        /// behind it. Without it a lake is a colour change with no edge — meadow green running to a
        /// waterline — and the bank the field carved does not read as a bank.</para>
        /// </summary>
        public static readonly Color32 SandTint = new Color(0.76f, 0.70f, 0.55f);

        /// <summary>
        /// How far above a water surface the sand reaches.
        ///
        /// <para>Three metres, and it is the second of the two limits rather than the only one — see
        /// <see cref="ShoreReach"/>. What it does here is stop the sand climbing a steep bank: where the
        /// ground rises fast out of the water, the band ends at the slope instead of running up
        /// it.</para>
        /// </summary>
        private const float ShoreHeight = 3f;

        /// <summary>
        /// And how far out from the waterline it reaches.
        ///
        /// <para>Eighteen metres, which is one to two triangles of this terrain — chunky, faceted, and
        /// exactly the register the rest of the world is drawn in. The height limit alone gave a band
        /// half again as large as the water itself, because the banks are carved to ease out over forty
        /// to seventy metres and almost all of that lies within three metres of the surface.</para>
        /// </summary>
        private const float ShoreReach = 18f;

        public const int GrassSubmesh = 0;

        /// <summary>Submesh for steep faces, meant for rock.</summary>
        public const int RockSubmesh = 1;

        public const int TileSubmeshCount = 2;

        /// <summary>
        /// Lists the tiles worth generating: those whose extent comes within
        /// <paramref name="corridorWidth"/> of the road, plus any that touch an
        /// <paramref name="extraRegions"/> footprint.
        ///
        /// <para>The extra regions exist so a *place* can have terrain wider than the corridor without the
        /// corridor being widened everywhere. The town's basin reaches 260 m out, which would need a 320 m
        /// corridor — and raising the constant would add that width along the whole five kilometres of
        /// pass, roughly doubling the tile count and the vegetation on it, for open hillside nobody drives
        /// within 200 m of. A dozen extra tiles where the town is costs almost nothing.</para>
        /// </summary>
        public static List<TerrainTileKey> ListTiles(
            MountainField field,
            in TerrainShape shape,
            float corridorWidth,
            IReadOnlyList<Bounds> extraRegions = null)
        {
            Bounds bounds = field.RoadBounds;
            float tileSize = TileSize(shape);

            float minX = bounds.min.x - corridorWidth;
            float maxX = bounds.max.x + corridorWidth;
            float minZ = bounds.min.z - corridorWidth;
            float maxZ = bounds.max.z + corridorWidth;

            for (int i = 0; extraRegions != null && i < extraRegions.Count; i++)
            {
                Bounds region = extraRegions[i];
                minX = Mathf.Min(minX, region.min.x);
                maxX = Mathf.Max(maxX, region.max.x);
                minZ = Mathf.Min(minZ, region.min.z);
                maxZ = Mathf.Max(maxZ, region.max.z);
            }

            int minColumn = Mathf.FloorToInt(minX / tileSize);
            int maxColumn = Mathf.FloorToInt(maxX / tileSize);
            int minRow = Mathf.FloorToInt(minZ / tileSize);
            int maxRow = Mathf.FloorToInt(maxZ / tileSize);

            var tiles = new List<TerrainTileKey>();

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    // Test the centre against the corridor width plus half a diagonal, so a tile whose
                    // corner alone reaches the road is still included.
                    float centreX = (column + 0.5f) * tileSize;
                    float centreZ = (row + 0.5f) * tileSize;
                    float reach = corridorWidth + tileSize * 0.71f;

                    if (field.DistanceToRoad(centreX, centreZ) <= reach
                        || TouchesRegion(extraRegions, column * tileSize, row * tileSize, tileSize))
                    {
                        tiles.Add(new TerrainTileKey(column, row));
                    }
                }
            }

            return tiles;
        }

        /// <summary>
        /// Whether a tile's plan extent overlaps any of the regions. Tested in plan only — the regions
        /// describe where a place is, not how tall it is, and a Y comparison would just be a way to miss
        /// tiles under a basin that sits below the bounds' centre.
        /// </summary>
        private static bool TouchesRegion(
            IReadOnlyList<Bounds> regions, float originX, float originZ, float tileSize)
        {
            for (int i = 0; regions != null && i < regions.Count; i++)
            {
                Bounds region = regions[i];

                if (originX + tileSize >= region.min.x && originX <= region.max.x
                    && originZ + tileSize >= region.min.z && originZ <= region.max.z)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Side length of a tile, snapped to a whole number of cells.</summary>
        public static float TileSize(in TerrainShape shape)
        {
            int cellsPerTile = Mathf.Max(2, Mathf.RoundToInt(shape.TileSize / shape.CellSize));
            return cellsPerTile * shape.CellSize;
        }

        /// <summary>World-space centre of a tile.</summary>
        public static Vector3 TileCentre(TerrainTileKey key, in TerrainShape shape, MountainField field)
        {
            float tileSize = TileSize(shape);
            float x = (key.Column + 0.5f) * tileSize;
            float z = (key.Row + 0.5f) * tileSize;
            return new Vector3(x, field.HeightAt(x, z), z);
        }

        /// <summary>
        /// Which diagonal a cell's quad is split along, from its four corner heights. The shorter one, so
        /// ridges do not look drawn on a grid, and — because the choice depends only on the heights — two
        /// tiles sharing an edge still agree.
        ///
        /// It is a named method rather than an inline expression because <see cref="SampleSurface"/> has to
        /// make the identical choice. A prop placed against the other diagonal floats or sinks by the full
        /// height difference across the cell, which on this terrain is a metre or two.
        /// </summary>
        public static bool SplitsForward(float y00, float y10, float y01, float y11)
        {
            return Mathf.Abs(y00 - y11) <= Mathf.Abs(y10 - y01);
        }

        /// <summary>
        /// The point and face normal of the finished terrain *mesh* at a world position.
        ///
        /// Not the same thing as <see cref="MountainField.HeightAt"/>, and the difference is the whole
        /// reason this exists: the mesh is a linear interpolation of the field across 12 m cells, so on a
        /// slope the two disagree by up to a metre or two. Anything meant to stand on the ground has to use
        /// this, or it hovers and sinks.
        ///
        /// The normal is the flat normal of the triangle actually hit, so it is the slope the mesh really
        /// has rather than the analytic slope of the field.
        /// </summary>
        public static void SampleSurface(
            MountainField field,
            in TerrainShape shape,
            float x,
            float z,
            out Vector3 point,
            out Vector3 normal)
        {
            float cell = shape.CellSize;

            // The same global lattice the tiles are built on — TileSize is a whole number of cells, so a
            // cell never straddles a tile boundary.
            float originX = Mathf.Floor(x / cell) * cell;
            float originZ = Mathf.Floor(z / cell) * cell;

            var c00 = new Vector3(originX, field.HeightAt(originX, originZ), originZ);
            var c10 = new Vector3(originX + cell, field.HeightAt(originX + cell, originZ), originZ);
            var c01 = new Vector3(originX, field.HeightAt(originX, originZ + cell), originZ + cell);
            var c11 = new Vector3(originX + cell, field.HeightAt(originX + cell, originZ + cell), originZ + cell);

            float u = (x - originX) / cell;
            float v = (z - originZ) / cell;

            Vector3 a, b, c;
            if (SplitsForward(c00.y, c10.y, c01.y, c11.y))
            {
                // Diagonal from (0,0) to (1,1); the same two triangles BuildTile emits.
                if (v >= u)
                {
                    a = c00; b = c01; c = c11;
                }
                else
                {
                    a = c00; b = c11; c = c10;
                }
            }
            else
            {
                // Diagonal from (1,0) to (0,1).
                if (u + v <= 1f)
                {
                    a = c00; b = c01; c = c10;
                }
                else
                {
                    a = c01; b = c11; c = c10;
                }
            }

            normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 0.000001f)
            {
                normal = Vector3.up;
                point = new Vector3(x, a.y, z);
                return;
            }

            normal.Normalize();
            if (normal.y < 0f)
            {
                normal = -normal;
            }

            // Evaluated on the plane of that triangle rather than through barycentric weights: it is the
            // same answer, and it cannot drift out of step with the normal computed just above.
            float height = a.y - (normal.x * (x - a.x) + normal.z * (z - a.z)) / normal.y;
            point = new Vector3(x, height, z);
        }

        /// <summary>
        /// Builds one tile. Two submeshes, split by face slope.
        ///
        /// Vertices land on a lattice defined in world space, not relative to the tile, and the height
        /// function is purely a function of position — so neighbouring tiles agree exactly along their
        /// shared edge. Sampling per-tile instead is the standard way to end up with cracks.
        /// </summary>
        public static Mesh BuildTile(
            TerrainTileKey key,
            MountainField field,
            in TerrainShape shape,
            string meshName)
        {
            float tileSize = TileSize(shape);
            int cells = Mathf.Max(2, Mathf.RoundToInt(tileSize / shape.CellSize));
            int corners = cells + 1;

            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            // Corner heights first, so each triangle is built from finished values and the two tiles
            // sharing an edge compute the identical number for it.
            var heights = new float[corners * corners];
            for (int row = 0; row < corners; row++)
            {
                for (int column = 0; column < corners; column++)
                {
                    float x = originX + column * shape.CellSize;
                    float z = originZ + row * shape.CellSize;
                    heights[row * corners + column] = field.HeightAt(x, z);
                }
            }

            int quadCount = cells * cells;
            var vertices = new List<Vector3>(quadCount * 6);
            var normals = new List<Vector3>(quadCount * 6);
            var uvs = new List<Vector2>(quadCount * 6);
            var triangles = new List<int>(quadCount * 6);
            var colours = new List<Color32>(quadCount * 6);

            float rockThreshold = Mathf.Cos(shape.RockSlopeThreshold * Mathf.Deg2Rad);

            // Whether this tile is near water at all, asked once rather than per triangle. Four tiles
            // in five are nowhere near a body, and the shore test is the only thing in this loop that
            // walks a list.
            bool nearWater = TouchesWater(field, originX, originZ, tileSize);

            for (int row = 0; row < cells; row++)
            {
                for (int column = 0; column < cells; column++)
                {
                    Vector3 c00 = Corner(originX, originZ, shape.CellSize, heights, corners, column, row);
                    Vector3 c10 = Corner(originX, originZ, shape.CellSize, heights, corners, column + 1, row);
                    Vector3 c01 = Corner(originX, originZ, shape.CellSize, heights, corners, column, row + 1);
                    Vector3 c11 = Corner(originX, originZ, shape.CellSize, heights, corners, column + 1, row + 1);

                    bool splitForward = SplitsForward(c00.y, c10.y, c01.y, c11.y);

                    if (splitForward)
                    {
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, c00, c01, c11);
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, c00, c11, c10);
                    }
                    else
                    {
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, c00, c01, c10);
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, c01, c11, c10);
                    }
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colours);
            mesh.subMeshCount = 1;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Whether this tile lies entirely under deep water, and can therefore be left unbuilt.
        ///
        /// <para>The sea bed is the one piece of terrain in the world nobody can ever see: the surface
        /// over it is opaque. Skipping those tiles is what makes a sea affordable — pushing the water out
        /// far enough that its far edge falls behind the fog costs about forty tiles of ground, each
        /// carrying four hundred triangles and a mesh collider for a view nobody has.</para>
        ///
        /// <para>A depth rather than mere submersion, and four metres rather than none, because the
        /// collider is worth keeping where a car can still reach it. The shallows are where somebody
        /// drives in; past four metres they are being fished out by <c>WaterHazard</c> either way. The
        /// water's own shading does not depend on this — it is sampled from the height field, not from
        /// the mesh — so a missing tile costs nothing but the ground.</para>
        ///
        /// <para>Sampled on a grid rather than at the four corners, because a tile can have all four
        /// corners deep and a sandbank in the middle of it.</para>
        /// </summary>
        public static bool IsDrowned(MountainField field, in TerrainShape shape, TerrainTileKey key)
        {
            IReadOnlyList<WaterBody> waters = field.Water;
            if (waters.Count == 0)
            {
                return false;
            }

            const float outOfSight = 4f;

            float tileSize = TileSize(shape);
            float originX = key.Column * tileSize;
            float originZ = key.Row * tileSize;

            const int steps = 6;

            for (int row = 0; row <= steps; row++)
            {
                for (int column = 0; column <= steps; column++)
                {
                    float x = originX + tileSize * column / steps;
                    float z = originZ + tileSize * row / steps;

                    if (!field.IsUnderWater(x, z, field.HeightAt(x, z) + outOfSight))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Whether any body of water's plan extent overlaps this tile.</summary>
        private static bool TouchesWater(MountainField field, float originX, float originZ, float tileSize)
        {
            IReadOnlyList<WaterBody> waters = field.Water;

            for (int i = 0; i < waters.Count; i++)
            {
                Bounds plan = waters[i].Plan;

                if (originX + tileSize >= plan.min.x && originX <= plan.max.x
                    && originZ + tileSize >= plan.min.z && originZ <= plan.max.z)
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector3 Corner(
            float originX,
            float originZ,
            float cellSize,
            float[] heights,
            int corners,
            int column,
            int row)
        {
            return new Vector3(
                originX + column * cellSize,
                heights[row * corners + column],
                originZ + row * cellSize);
        }

        /// <summary>
        /// One flat-shaded triangle: its own vertices so it keeps a single normal, which is what gives
        /// the faceted look the art direction asks for.
        /// </summary>
        private static void AddTriangle(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<Color32> colours,
            List<int> triangles,
            float rockThreshold,
            MountainField field,
            bool nearWater,
            Vector3 a,
            Vector3 b,
            Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 0.000001f)
            {
                return;
            }

            normal.Normalize();
            if (normal.y < 0f)
            {
                (b, c) = (c, b);
                normal = -normal;
            }

            int baseIndex = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            // World-space XZ, so the texture tiles consistently across every tile.
            uvs.Add(new Vector2(a.x, a.z) * 0.05f);
            uvs.Add(new Vector2(b.x, b.z) * 0.05f);
            uvs.Add(new Vector2(c.x, c.z) * 0.05f);

            // Grass or rock as a colour rather than as a submesh. Every tile in the world used to pay
            // two material slots for this whether or not it had a steep face on it — the one place
            // MergeTinted's lesson had not been applied, and the only per-tile cost that grows with the
            // size of the world rather than with the size of a town.
            //
            // Sand goes over both, steep faces included. A bank the field has carved is a shallow
            // thing by construction — the ease runs forty to seventy metres for a drop of a few — so a
            // face at the water that does read as steep is a spit or a cut, and grey rock there breaks
            // the shoreline into pieces rather than describing it.
            Color32 tint = normal.y < rockThreshold ? RockTint : GrassTint;

            if (nearWater)
            {
                // The centroid, not a corner: this is one flat-shaded triangle with one colour, and
                // asking at a corner makes the tint depend on which corner the winding happened to put
                // first, which is how you get single triangles of beach out in a meadow.
                Vector3 centre = (a + b + c) * (1f / 3f);

                if (field.IsShore(centre.x, centre.z, centre.y, ShoreHeight, ShoreReach))
                {
                    tint = SandTint;
                }
            }

            colours.Add(tint);
            colours.Add(tint);
            colours.Add(tint);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }
    }
}
