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
        /// Ground above a region's snow line.
        ///
        /// <para>Free for the reason <see cref="SandTint"/> is free: a fourth choice inside a comparison
        /// this method was making anyway, on the one shared vertex-tinted material, so nine hundred
        /// metres of mountain draws in the same call as the valley under it. No material, no draw call,
        /// no vertices.</para>
        ///
        /// <para>Blue rather than white. Snow lit by a low sun is warm on one face and blue in shadow,
        /// and a flat white reads as a hole in the frame — the terrain shader is unlit-ish enough that
        /// a pure white face has nowhere left to go.</para>
        /// </summary>
        public static readonly Color32 SnowTint = new Color(0.84f, 0.87f, 0.92f);

        /// <summary>
        /// How far the ground's colour wanders, per kind, as a fraction of the tint itself.
        ///
        /// <para><b>This is the woods' argument applied to the thing under them.</b> Every conifer in
        /// the world used to share one green and every broadleaf another, and a hillside of four hundred
        /// trees was four hundred copies of two tones; three greens each fixed it and cost nothing,
        /// because a colour on a vertex is free. The terrain never got the same treatment, and it is far
        /// more of the frame than the trees are — a meadow was one flat colour from the verge to the fog
        /// wall, and a rock face was one slab.</para>
        ///
        /// <para><b>Three terms, and they are three because they do different jobs.</b> <c>Patch</c>
        /// moves value over roughly two hundred metres, which is what makes one meadow a different
        /// meadow from the next. <c>Warmth</c> moves red against blue on a <i>differently scaled</i>
        /// lookup, so a field can go yellower without also going lighter — one lookup driving both
        /// gives a single light-to-dark axis, which reads as shading rather than as ground.
        /// <c>Facet</c> is per triangle, and it is the one that makes flat-shaded terrain read as
        /// crafted rather than as a solid: it is the whole visual idiom of the reference art and this
        /// world had none of it.</para>
        ///
        /// <para><b>Rock leans on the facet term and snow leans away from all three.</b> Stone is
        /// faceted by nature, so the per-triangle break is the honest part of it. Snow is the brightest
        /// thing in the world and variation on it reads as dirt rather than as drift — its line is
        /// already broken by <see cref="SnowLineJitter"/>, which is the term that was doing this job
        /// there before this existed.</para>
        /// </summary>
        private readonly struct Variation
        {
            public readonly float Patch;
            public readonly float Warmth;
            public readonly float Facet;

            public Variation(float patch, float warmth, float facet)
            {
                Patch = patch;
                Warmth = warmth;
                Facet = facet;
            }
        }

        private static readonly Variation GrassVariation = new Variation(0.20f, 0.13f, 0.055f);
        private static readonly Variation RockVariation = new Variation(0.15f, 0.06f, 0.075f);
        private static readonly Variation SandVariation = new Variation(0.10f, 0.06f, 0.040f);
        private static readonly Variation SnowVariation = new Variation(0.05f, 0.03f, 0.025f);

        /// <summary>Scale of the value wander. About a hundred and ninety metres.</summary>
        private const float PatchScale = 0.0052f;

        /// <summary>
        /// Scale of the warmth wander, deliberately not a multiple of <see cref="PatchScale"/>.
        ///
        /// <para>Two lookups that share a period beat against each other and the ground comes out in
        /// stripes at the difference frequency. This one is also the coarser of the two, because how
        /// warm a country is changes over a longer distance than how bright a field is.</para>
        /// </summary>
        private const float WarmthScale = 0.0031f;

        /// <summary>
        /// How finely a triangle's centroid is quantised before it is hashed, samples per metre.
        ///
        /// <para>Quarter-metre. Cells are twelve metres, so no two triangles in the world can land on
        /// the same key by accident, and a quantised integer is what makes this a pure function of
        /// position — the same rule <c>SurfaceRelief</c> states for itself. Hashing the float directly
        /// would make the answer depend on the last bit of an accumulated sum.</para>
        /// </summary>
        private const float FacetQuantisation = 4f;

        /// <summary>
        /// How much of the value wander a parcel keeps.
        ///
        /// <para>A fifth. A ploughed field is uniform, and that uniformity is exactly what makes it
        /// read as a field rather than as ground — so the patch term, which exists to say "this meadow
        /// is not that meadow", has to get out of the way where somebody has already said which field
        /// this is. Not nothing, though: a dead flat parcel beside a wandering hillside reads as a
        /// decal.</para>
        ///
        /// <para>The facet term is untouched, so a field is a field with texture in it.</para>
        /// </summary>
        private const float ParcelFlatness = 0.2f;

        /// <summary>
        /// How many triangles of a tile came out each of the kinds the build reports.
        ///
        /// <para><b>Counted while they are chosen, and the reason is that they used to be counted
        /// afterwards by colour.</b> <c>PrototypeSetup</c> read <c>mesh.colors32</c> back three times
        /// per tile and matched exact rgb — which was correct for as long as a kind was exactly one
        /// colour, and stopped being correct the moment <see cref="Variation"/> existed. A tolerance
        /// would not have saved it either: the blossom drift and the snow are forty levels apart in a
        /// world where either may wander thirty, so the two would have had to be told apart by a
        /// distance that is not reliably smaller than their separation.</para>
        ///
        /// <para>It is also cheaper by a wide margin. <c>colors32</c> allocates the whole array on every
        /// read, and there are fifteen hundred tiles and three counters.</para>
        /// </summary>
        public struct TerrainTintCounts
        {
            public int Triangles;
            public int Sand;
            public int Snow;
            public int Petal;

            /// <summary>
            /// Ground standing above a region's snow line, whether or not it came out snow.
            ///
            /// <para><b>Here so the snow line can be reported as a share rather than as an
            /// adjective.</b> Snow is skipped on faces past <see cref="TerrainShape.RockSlopeThreshold"/>
            /// on purpose — uniform white above a height is a cake, and the flanks between stacked legs
            /// should come out bare rock — but that test is one number away from taking the lot, and
            /// CLAUDE.md carried a claim for some time that it had. Counting what was eligible against
            /// what was painted is the difference between knowing and saying.</para>
            /// </summary>
            public int AboveSnowLine;
        }

        /// <summary>
        /// How far the snow line wanders, metres either side.
        ///
        /// <para>A snow line laid on flat is a contour drawn round a mountain, which is what a map does
        /// and not what weather does. One noise lookup breaks it into drifts and bare patches — the same
        /// trick <c>VegetationShape.TreeLineJitter</c> plays on the tree line, and for the same
        /// reason.</para>
        /// </summary>
        private const float SnowLineJitter = 22f;

        /// <summary>Scale of that wander. Coarse — drifts are tens of metres across, not metres.</summary>
        private const float SnowJitterScale = 0.0035f;

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
            string meshName,
            out TerrainTintCounts counts,
            LandRegion region = null)
        {
            counts = default;

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

            // Asked once for the tile, for the same reason: a region is a smooth field and its weight
            // cannot jump inside 168 m, so a tile that is nowhere near one can skip the whole business
            // per triangle rather than per tile. Sampled at the four corners and the middle, because a
            // tile can clip the edge of a region with its centre well outside it.
            LandRegion tileRegion = TouchesRegion(region, originX, originZ, tileSize) ? region : null;

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
                            field, nearWater, tileRegion, ref counts, c00, c01, c11);
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, tileRegion, ref counts, c00, c11, c10);
                    }
                    else
                    {
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, tileRegion, ref counts, c00, c01, c10);
                        AddTriangle(vertices, normals, uvs, colours, triangles, rockThreshold,
                            field, nearWater, tileRegion, ref counts, c01, c11, c10);
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
            LandRegion region,
            ref TerrainTintCounts counts,
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
            bool steep = normal.y < rockThreshold;
            Color32 tint = steep ? RockTint : GrassTint;
            Variation variation = steep ? RockVariation : GrassVariation;

            // How much of the value wander survives here. A parcel takes most of it away — see
            // ParcelFlatness — and nothing else does.
            float patchShare = 1f;

            // The centroid, not a corner: this is one flat-shaded triangle with one colour, and asking
            // at a corner makes the answer depend on which corner the winding happened to put first,
            // which is how you get single triangles of beach out in a meadow — or one field's colour on
            // a sliver of the next one's.
            Vector3 centre = (a + b + c) * (1f / 3f);

            if (region != null)
            {
                float weight = region.Weight(centre.x, centre.z);

                if (weight > 0f)
                {
                    Color32 regional = steep ? region.Ground.Rock : FieldTint(region, centre);
                    tint = Color32.Lerp(tint, regional, weight);

                    // The blossom drift, recognised here rather than counted back off the finished mesh
                    // by colour: this is the one place the palette's own entry is in hand, before
                    // anything has wandered.
                    if (!steep && Same(regional, LandRegion.BahcePetal))
                    {
                        counts.Petal++;
                    }

                    // Only where somebody has actually laid fields out. A region without them falls
                    // through to its own meadow, which is ground and wants to wander like ground.
                    if (!steep && region.Ground.Fields != null && region.Ground.Fields.Length > 0)
                    {
                        patchShare = Mathf.Lerp(patchShare, ParcelFlatness, weight);
                    }
                }
            }

            bool wasSnow = false;

            // Snow, where the region has a line and the ground is not too steep to hold any.
            //
            // The slope test is what makes it a mountain rather than a white sheet: MountainField gives
            // the face between two stacked switchback legs a local peak far past RockSlopeThreshold, so
            // the flanks come out bare rock with snow lying on everything gentler either side of them.
            // Uniform white above a line is a cake.
            // The steep test moved off this condition and onto the tint below, so that ground above the
            // line is counted whether or not it was allowed to hold any — see TerrainTintCounts.
            if (region != null && !float.IsNaN(region.SnowLineElevation)
                && region.Weight(centre.x, centre.z) > 0f)
            {
                float jitter = (Mathf.PerlinNoise(
                    (centre.x + 512f) * SnowJitterScale,
                    (centre.z + 512f) * SnowJitterScale) - 0.5f) * 2f * SnowLineJitter;

                if (centre.y > region.SnowLineElevation + jitter)
                {
                    counts.AboveSnowLine++;
                }

                if (!steep && centre.y > region.SnowLineElevation + jitter)
                {
                    tint = SnowTint;
                    variation = SnowVariation;
                    patchShare = 1f;
                    wasSnow = true;
                    counts.Snow++;
                }
            }

            if (nearWater)
            {
                if (field.IsShore(centre.x, centre.z, centre.y, ShoreHeight, ShoreReach))
                {
                    // Over everything, region and snow included. A shore is a shore in any country, and
                    // a ploughed field running to a waterline reads as a bug rather than as a bank.
                    // Nothing in this world has both a shore and a snow line, but the order is written
                    // down rather than left to chance for the day something does.
                    tint = SandTint;
                    variation = SandVariation;
                    patchShare = 1f;

                    // Sand goes over the snow, so a triangle that was counted as snow one branch above
                    // is no longer snow. Taking the count back is what keeps the two lines in the build
                    // log adding up to what the mesh actually holds.
                    if (counts.Snow > 0 && wasSnow)
                    {
                        counts.Snow--;
                    }

                    counts.Sand++;
                }
            }

            counts.Triangles++;
            tint = Weathered(tint, centre, variation, patchShare);

            colours.Add(tint);
            colours.Add(tint);
            colours.Add(tint);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }

        /// <summary>
        /// Wanders a ground colour, so a hillside is a hillside rather than one flat tone.
        ///
        /// <para>Pure function of position, no state and no seed, which is what lets two tiles built in
        /// any order agree along their shared edge — the same rule the height field and
        /// <c>SurfaceRelief</c> both hold to, and the reason this is a lookup rather than a
        /// <c>System.Random</c> walked down the triangle list.</para>
        ///
        /// <para><b>The wander is multiplicative and the warmth is a rotation about it</b>, so a dark
        /// tint stays dark and a bright one stays bright: an additive term of the same size washes the
        /// snow out and leaves the ploughed earth almost untouched, because the same number is a third
        /// of one and a twentieth of the other.</para>
        ///
        /// <para>Alpha is carried through untouched and it matters: the vegetation shader reads
        /// <c>1 - alpha</c> as its wind mask, terrain writes 255 to mean rigid, and a colour rebuilt
        /// without its alpha would set every hillside in the world swaying. That is the trap
        /// <c>VegetationMeshBuffer.MergeTinted</c> has already been through once.</para>
        /// </summary>
        private static Color32 Weathered(Color32 tint, Vector3 at, in Variation variation, float patchShare)
        {
            // The +512 shift is the one VegetationBuilder.Clump and the snow line already carry: Unity's
            // Perlin mirrors about the origin, and this world starts at negative z.
            float value = Mathf.PerlinNoise((at.x + 512f) * PatchScale, (at.z + 512f) * PatchScale);
            float warm = Mathf.PerlinNoise((at.z + 811f) * WarmthScale, (at.x + 811f) * WarmthScale);

            // x and z swapped on the second lookup as well as offset. Two Perlin fields at the same
            // orientation share their lattice's diagonals however far apart their offsets are, and the
            // ground comes out with a faint grain running one way across the whole world.
            int keyX = Mathf.RoundToInt(at.x * FacetQuantisation);
            int keyZ = Mathf.RoundToInt(at.z * FacetQuantisation);

            float wander = 1f
                + (value - 0.5f) * 2f * variation.Patch * patchShare
                + (Hash(keyX, keyZ) - 0.5f) * 2f * variation.Facet;

            float warmth = (warm - 0.5f) * 2f * variation.Warmth * patchShare;

            Color colour = tint;

            return new Color(
                Mathf.Clamp01(colour.r * wander * (1f + warmth)),
                Mathf.Clamp01(colour.g * wander),
                Mathf.Clamp01(colour.b * wander * (1f - warmth)),
                colour.a);
        }

        /// <summary>
        /// Whether two palette entries are the same colour, ignoring alpha.
        ///
        /// <para>Exact, and it can be: both sides come straight off a <c>GroundPalette</c>, before
        /// anything has wandered. This is the comparison that used to be made against the finished mesh,
        /// where it could not stay exact.</para>
        /// </summary>
        private static bool Same(Color32 a, Color32 b)
        {
            return a.r == b.r && a.g == b.g && a.b == b.b;
        }

        /// <summary>
        /// A stable pseudo-random number for one triangle, from its quantised centroid.
        ///
        /// <para>FNV-1a with an avalanche, the same shape <c>LandRegion.Hash</c> and
        /// <c>VegetationBuilder.Hash</c> already use — so a rebuild is byte-identical and the order the
        /// tiles happen to be built in cannot change what the ground looks like.</para>
        /// </summary>
        private static float Hash(int x, int z)
        {
            unchecked
            {
                uint hash = 2166136261u;

                hash = (hash ^ (uint)x) * 16777619u;
                hash = (hash ^ (uint)z) * 16777619u;
                hash ^= hash >> 13;
                hash *= 0x5bd1e995u;
                hash ^= hash >> 15;

                return (hash & 0xFFFFFFu) / (float)0x1000000;
            }
        }

        /// <summary>
        /// The colour of the field a point stands in, falling back to the region's own meadow where the
        /// palette carries no fields.
        /// </summary>
        private static Color32 FieldTint(LandRegion region, Vector3 at)
        {
            int parcel = region.Parcel(at.x, at.z);

            return parcel < 0 ? region.Ground.Grass : region.Ground.Fields[parcel];
        }

        /// <summary>
        /// Whether a region reaches this tile at all, asked once against the tile's bounding circle.
        ///
        /// <para>The circle rather than the corners: sampling the weight at five points can miss a
        /// region that clips one corner of a 168 m tile, and a tile that quietly kept the world's colours
        /// while its neighbour changed is a seam through a meadow.</para>
        /// </summary>
        private static bool TouchesRegion(LandRegion region, float originX, float originZ, float tileSize)
        {
            if (region == null)
            {
                return false;
            }

            float half = tileSize * 0.5f;

            // Half the diagonal, so the circle contains the square.
            return region.Reaches(originX + half, originZ + half, half * Mathf.Sqrt(2f));
        }
    }
}
