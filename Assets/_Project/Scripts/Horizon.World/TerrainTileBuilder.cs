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
        public const int GrassSubmesh = 0;

        /// <summary>Submesh for steep faces, meant for rock.</summary>
        public const int RockSubmesh = 1;

        public const int TileSubmeshCount = 2;

        /// <summary>
        /// Lists the tiles worth generating: those whose extent comes within
        /// <paramref name="corridorWidth"/> of the road.
        /// </summary>
        public static List<TerrainTileKey> ListTiles(
            MountainField field,
            in TerrainShape shape,
            float corridorWidth)
        {
            Bounds bounds = field.RoadBounds;
            float tileSize = TileSize(shape);

            int minColumn = Mathf.FloorToInt((bounds.min.x - corridorWidth) / tileSize);
            int maxColumn = Mathf.FloorToInt((bounds.max.x + corridorWidth) / tileSize);
            int minRow = Mathf.FloorToInt((bounds.min.z - corridorWidth) / tileSize);
            int maxRow = Mathf.FloorToInt((bounds.max.z + corridorWidth) / tileSize);

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

                    if (field.DistanceToRoad(centreX, centreZ) <= reach)
                    {
                        tiles.Add(new TerrainTileKey(column, row));
                    }
                }
            }

            return tiles;
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
            var grass = new List<int>(quadCount * 6);
            var rock = new List<int>(quadCount * 3);

            float rockThreshold = Mathf.Cos(shape.RockSlopeThreshold * Mathf.Deg2Rad);

            for (int row = 0; row < cells; row++)
            {
                for (int column = 0; column < cells; column++)
                {
                    Vector3 c00 = Corner(originX, originZ, shape.CellSize, heights, corners, column, row);
                    Vector3 c10 = Corner(originX, originZ, shape.CellSize, heights, corners, column + 1, row);
                    Vector3 c01 = Corner(originX, originZ, shape.CellSize, heights, corners, column, row + 1);
                    Vector3 c11 = Corner(originX, originZ, shape.CellSize, heights, corners, column + 1, row + 1);

                    // Split along the shorter diagonal so ridges do not look drawn on a grid. The choice
                    // depends only on the four heights, so both tiles sharing an edge still agree.
                    bool splitForward = Mathf.Abs(c00.y - c11.y) <= Mathf.Abs(c10.y - c01.y);

                    if (splitForward)
                    {
                        AddTriangle(vertices, normals, uvs, grass, rock, rockThreshold, c00, c01, c11);
                        AddTriangle(vertices, normals, uvs, grass, rock, rockThreshold, c00, c11, c10);
                    }
                    else
                    {
                        AddTriangle(vertices, normals, uvs, grass, rock, rockThreshold, c00, c01, c10);
                        AddTriangle(vertices, normals, uvs, grass, rock, rockThreshold, c01, c11, c10);
                    }
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = TileSubmeshCount;
            mesh.SetTriangles(grass, GrassSubmesh);
            mesh.SetTriangles(rock, RockSubmesh);
            mesh.RecalculateBounds();
            return mesh;
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
            List<int> grass,
            List<int> rock,
            float rockThreshold,
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

            List<int> target = normal.y < rockThreshold ? rock : grass;
            target.Add(baseIndex);
            target.Add(baseIndex + 1);
            target.Add(baseIndex + 2);
        }
    }
}
