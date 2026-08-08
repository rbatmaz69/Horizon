using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>
    /// Generates the road ribbon and the low-poly valley around it from an <see cref="IRoadPath"/>.
    ///
    /// Meshes are built once at edit time and saved as assets — nothing here should run during play.
    /// The terrain is flat-shaded on purpose: every triangle gets its own vertices so each face keeps
    /// a single normal, which is what produces the faceted look the art direction asks for.
    /// </summary>
    public static class RoadMeshBuilder
    {
        /// <summary>Submesh 0 of the terrain: shallow faces, meant for a grass material.</summary>
        public const int TerrainGrassSubmesh = 0;

        /// <summary>Submesh 1 of the terrain: steep faces, meant for a rock material.</summary>
        public const int TerrainRockSubmesh = 1;

        /// <summary>Builds the driveable road surface, with a dropped verge either side.</summary>
        public static Mesh BuildRoad(IRoadPath path, in RoadShape shape, string meshName = "RoadMesh")
        {
            float length = path.Length;
            int sectionCount = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, shape.StepLength)) + 1);

            var vertices = new List<Vector3>(sectionCount * 4);
            var normals = new List<Vector3>(sectionCount * 4);
            var uvs = new List<Vector2>(sectionCount * 4);
            var triangles = new List<int>(sectionCount * 18);

            float outerHalfWidth = shape.HalfWidth + shape.ShoulderWidth;

            for (int i = 0; i < sectionCount; i++)
            {
                float distance = length * i / (sectionCount - 1);
                Vector3 center = path.GetPositionAtDistance(distance) + Vector3.up * shape.SurfaceLift;
                Vector3 right = path.GetRightAtDistance(distance);

                Vector3 drop = Vector3.down * shape.ShoulderDrop;
                vertices.Add(center - right * outerHalfWidth + drop);
                vertices.Add(center - right * shape.HalfWidth);
                vertices.Add(center + right * shape.HalfWidth);
                vertices.Add(center + right * outerHalfWidth + drop);

                // Road surface normals stay smooth along the length; faceting is the terrain's job.
                Vector3 up = Vector3.Cross(path.GetDirectionAtDistance(distance), right).normalized;
                if (up.y < 0f)
                {
                    up = -up;
                }

                for (int v = 0; v < 4; v++)
                {
                    normals.Add(up);
                }

                float textureV = distance / Mathf.Max(0.1f, shape.TextureLength);
                uvs.Add(new Vector2(0f, textureV));
                uvs.Add(new Vector2(0.12f, textureV));
                uvs.Add(new Vector2(0.88f, textureV));
                uvs.Add(new Vector2(1f, textureV));

                if (i == 0)
                {
                    continue;
                }

                int current = i * 4;
                int previous = current - 4;
                for (int strip = 0; strip < 3; strip++)
                {
                    AddQuad(
                        triangles,
                        previous + strip,
                        previous + strip + 1,
                        current + strip,
                        current + strip + 1);
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds the surrounding terrain as a flat-shaded grid that meets the road at road level.
        /// Returns a mesh with two submeshes, split by face slope — see the Submesh constants.
        /// </summary>
        public static Mesh BuildTerrain(IRoadPath path, in TerrainShape shape, string meshName = "TerrainMesh")
        {
            SampleRoad(path, out Vector3[] roadPoints, out Vector3[] roadRights);
            ComputeGridBounds(roadPoints, shape.Margin, out Vector3 origin, out int columns, out int rows, shape.CellSize);

            // Height field first, so triangles can be built from finished corner heights.
            var heights = new float[columns * rows];
            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < columns; x++)
                {
                    float worldX = origin.x + x * shape.CellSize;
                    float worldZ = origin.z + z * shape.CellSize;
                    heights[z * columns + x] = SampleHeight(worldX, worldZ, roadPoints, roadRights, shape);
                }
            }

            int quadCount = (columns - 1) * (rows - 1);
            var vertices = new List<Vector3>(quadCount * 6);
            var normals = new List<Vector3>(quadCount * 6);
            var uvs = new List<Vector2>(quadCount * 6);
            var grassTriangles = new List<int>(quadCount * 6);
            var rockTriangles = new List<int>(quadCount * 3);

            float rockThreshold = Mathf.Cos(shape.RockSlopeThreshold * Mathf.Deg2Rad);

            for (int z = 0; z < rows - 1; z++)
            {
                for (int x = 0; x < columns - 1; x++)
                {
                    Vector3 c00 = GridPoint(origin, shape.CellSize, heights, columns, x, z);
                    Vector3 c10 = GridPoint(origin, shape.CellSize, heights, columns, x + 1, z);
                    Vector3 c01 = GridPoint(origin, shape.CellSize, heights, columns, x, z + 1);
                    Vector3 c11 = GridPoint(origin, shape.CellSize, heights, columns, x + 1, z + 1);

                    // Split each quad along the shorter diagonal: it keeps ridges from looking like
                    // they were drawn on a grid.
                    bool splitForward = Mathf.Abs(c00.y - c11.y) <= Mathf.Abs(c10.y - c01.y);
                    if (splitForward)
                    {
                        AddFlatTriangle(vertices, normals, uvs, grassTriangles, rockTriangles, rockThreshold, c00, c01, c11);
                        AddFlatTriangle(vertices, normals, uvs, grassTriangles, rockTriangles, rockThreshold, c00, c11, c10);
                    }
                    else
                    {
                        AddFlatTriangle(vertices, normals, uvs, grassTriangles, rockTriangles, rockThreshold, c00, c01, c10);
                        AddFlatTriangle(vertices, normals, uvs, grassTriangles, rockTriangles, rockThreshold, c01, c11, c10);
                    }
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(grassTriangles, TerrainGrassSubmesh);
            mesh.SetTriangles(rockTriangles, TerrainRockSubmesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Samples the road at roughly one point per 4 m, with its right vector.</summary>
        private static void SampleRoad(IRoadPath path, out Vector3[] points, out Vector3[] rights)
        {
            float length = path.Length;
            int count = Mathf.Max(2, Mathf.CeilToInt(length / 4f) + 1);

            points = new Vector3[count];
            rights = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                float distance = length * i / (count - 1);
                points[i] = path.GetPositionAtDistance(distance);
                rights[i] = path.GetRightAtDistance(distance);
            }
        }

        private static void ComputeGridBounds(
            Vector3[] roadPoints,
            float margin,
            out Vector3 origin,
            out int columns,
            out int rows,
            float cellSize)
        {
            Vector3 min = roadPoints[0];
            Vector3 max = roadPoints[0];
            for (int i = 1; i < roadPoints.Length; i++)
            {
                min = Vector3.Min(min, roadPoints[i]);
                max = Vector3.Max(max, roadPoints[i]);
            }

            min -= new Vector3(margin, 0f, margin);
            max += new Vector3(margin, 0f, margin);

            origin = new Vector3(min.x, 0f, min.z);
            columns = Mathf.Max(2, Mathf.CeilToInt((max.x - min.x) / cellSize) + 1);
            rows = Mathf.Max(2, Mathf.CeilToInt((max.z - min.z) / cellSize) + 1);
        }

        private static Vector3 GridPoint(Vector3 origin, float cellSize, float[] heights, int columns, int x, int z)
        {
            return new Vector3(
                origin.x + x * cellSize,
                heights[z * columns + x],
                origin.z + z * cellSize);
        }

        /// <summary>
        /// Terrain height at a world XZ. Flat at road level near the road, then rising on the inside
        /// of the curve and falling away on the outside.
        /// </summary>
        private static float SampleHeight(
            float worldX,
            float worldZ,
            Vector3[] roadPoints,
            Vector3[] roadRights,
            in TerrainShape shape)
        {
            var point = new Vector3(worldX, 0f, worldZ);

            int nearest = 0;
            float nearestSqr = float.MaxValue;
            for (int i = 0; i < roadPoints.Length; i++)
            {
                float dx = roadPoints[i].x - worldX;
                float dz = roadPoints[i].z - worldZ;
                float sqr = dx * dx + dz * dz;
                if (sqr < nearestSqr)
                {
                    nearestSqr = sqr;
                    nearest = i;
                }
            }

            Vector3 roadPoint = roadPoints[nearest];
            float roadLevel = roadPoint.y;
            float distance = Mathf.Sqrt(nearestSqr);

            // The flat shelf must be at least two cells wide. Otherwise a single triangle can have
            // one corner pinned at road level and the next already climbing, and it cuts straight
            // through the asphalt.
            float verge = Mathf.Max(shape.VergeWidth, shape.CellSize * 2f);

            float away = distance - verge;
            if (away <= 0f)
            {
                return roadLevel;
            }

            float blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(away / Mathf.Max(1f, shape.BlendDistance)));

            float ridge = (Mathf.PerlinNoise(worldX * shape.RidgeScale, worldZ * shape.RidgeScale) - 0.5f)
                          * 2f * shape.RidgeAmplitude;
            float detail = (Mathf.PerlinNoise(worldX * shape.DetailScale, worldZ * shape.DetailScale) - 0.5f)
                           * 2f * shape.DetailAmplitude;

            float relief = Mathf.Min(shape.MaxRelief, away * shape.SlopeRise) + ridge + detail;

            // Which side of the road are we on? Uphill inside, valley outside.
            float side = Vector3.Dot(point - new Vector3(roadPoint.x, 0f, roadPoint.z), roadRights[nearest]);
            if (side >= 0f)
            {
                relief = -relief * shape.ValleyDepth;
            }

            return roadLevel + blend * relief;
        }

        private static void AddFlatTriangle(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> grassTriangles,
            List<int> rockTriangles,
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
                // Keep winding consistent so faces point up regardless of the diagonal chosen.
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

            // World-space XZ as UV, so tiling stays consistent across the whole terrain.
            uvs.Add(new Vector2(a.x, a.z) * 0.05f);
            uvs.Add(new Vector2(b.x, b.z) * 0.05f);
            uvs.Add(new Vector2(c.x, c.z) * 0.05f);

            List<int> target = normal.y < rockThreshold ? rockTriangles : grassTriangles;
            target.Add(baseIndex);
            target.Add(baseIndex + 1);
            target.Add(baseIndex + 2);
        }

        /// <summary>
        /// Emits the two triangles of a road quad. Unity treats <c>Cross(b - a, c - a)</c> as the
        /// face normal, so the order here is what makes the surface face upwards rather than down.
        /// </summary>
        private static void AddQuad(
            List<int> triangles,
            int previousLeft,
            int previousRight,
            int currentLeft,
            int currentRight)
        {
            triangles.Add(previousLeft);
            triangles.Add(currentLeft);
            triangles.Add(previousRight);

            triangles.Add(previousRight);
            triangles.Add(currentLeft);
            triangles.Add(currentRight);
        }
    }
}
