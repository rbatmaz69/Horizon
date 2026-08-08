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

        /// <summary>Submesh carrying the paved surface and its markings.</summary>
        public const int AsphaltSubmesh = 0;

        /// <summary>Submesh carrying the gravel shoulders.</summary>
        public const int ShoulderSubmesh = 1;

        public const int RoadSubmeshCount = 2;

        /// <summary>Metres of shoulder covered by one tile of the gravel texture.</summary>
        private const float ShoulderTextureScale = 2.5f;

        /// <summary>u of the dashed centre-line variant in the surface texture atlas.</summary>
        private const float DashedVariantU = 0f;

        /// <summary>u of the solid centre-line variant.</summary>
        private const float SolidVariantU = 0.5f;

        /// <summary>Width in u of one variant.</summary>
        private const float VariantWidthU = 0.5f;

        private const int RingVertexCount = 9;

        /// <summary>
        /// First vertex of each quad strip across the section. The asphalt edges appear twice — once
        /// for the shoulder and once for the asphalt — because one edge has to carry two different sets
        /// of texture coordinates. The duplication also gives the road a hard edge against the verge,
        /// which is most of what makes it read as a road rather than a ribbon.
        /// </summary>
        private static readonly int[] StripStart = { 0, 2, 3, 4, 5, 7 };

        private static readonly bool[] StripIsShoulder = { true, false, false, false, false, true };

        /// <summary>
        /// Builds the carriageway: two lanes with painted markings, a cambered surface, and gravel
        /// shoulders as a separate submesh.
        /// </summary>
        public static Mesh BuildRoad(IRoadPath path, in RoadShape shape, string meshName = "RoadMesh")
        {
            float length = path.Length;
            int sectionCount = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, shape.StepLength)) + 1);

            bool[] solidLine = ResolveLineVariants(path, shape, sectionCount, length);

            // Rows of vertices. A change of marking variant emits the section twice — see the note on
            // BuildRows for why.
            BuildRows(sectionCount, length, solidLine, out float[] rowDistance, out bool[] rowSolid,
                out bool[] rowStartsStrip);

            int rowCount = rowDistance.Length;
            var vertices = new List<Vector3>(rowCount * RingVertexCount);
            var normals = new List<Vector3>(rowCount * RingVertexCount);
            var uvs = new List<Vector2>(rowCount * RingVertexCount);
            var asphaltTriangles = new List<int>(rowCount * 24);
            var shoulderTriangles = new List<int>(rowCount * 12);

            for (int row = 0; row < rowCount; row++)
            {
                AppendRing(path, shape, rowDistance[row], rowSolid[row], vertices, normals, uvs);
            }

            for (int row = 1; row < rowCount; row++)
            {
                // No quad across a variant change: the two rows sit at the same place but map to
                // different halves of the texture atlas.
                if (rowStartsStrip[row])
                {
                    continue;
                }

                int previous = (row - 1) * RingVertexCount;
                int current = row * RingVertexCount;

                for (int strip = 0; strip < StripStart.Length; strip++)
                {
                    int k = StripStart[strip];
                    List<int> target = StripIsShoulder[strip] ? shoulderTriangles : asphaltTriangles;

                    AddQuad(target, previous + k, previous + k + 1, current + k, current + k + 1);
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = RoadSubmeshCount;
            mesh.SetTriangles(asphaltTriangles, AsphaltSubmesh);
            mesh.SetTriangles(shoulderTriangles, ShoulderSubmesh);

            // Normals are assigned, never recalculated. RecalculateNormals averages over the triangles
            // that share a vertex, and the duplicated rows at a marking change share fewer than an
            // ordinary row does — which put a visible crease across the carriageway at every one of
            // them. Deriving the normal from the cross-section instead makes it a function of position,
            // so the surface is seamless along the road while the asphalt-to-verge edge stays crisp.
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Slope of the parabolic camber at a distance across the carriageway.</summary>
        private static float CrownSlope(float across, float halfWidth, float crown)
        {
            if (halfWidth < 0.01f)
            {
                return 0f;
            }

            return -2f * crown * across / (halfWidth * halfWidth);
        }

        /// <summary>
        /// Decides where the centre line is solid, from the corner radius, with hysteresis so a corner
        /// hovering near the threshold does not alternate every few metres.
        /// </summary>
        private static bool[] ResolveLineVariants(IRoadPath path, in RoadShape shape, int sectionCount, float length)
        {
            var solid = new bool[sectionCount];
            bool current = false;

            for (int i = 0; i < sectionCount; i++)
            {
                float distance = length * i / (sectionCount - 1);

                // Measured over a longer arc than the default: the path is a Catmull-Rom curve and its
                // heading wobbles slightly between control points, which over a short arc reads as a
                // tight corner and would switch the marking on and off along a straight.
                float radius = path.GetRadiusAtDistance(distance, 10f);

                if (current)
                {
                    if (radius > shape.DashedLineAboveRadius)
                    {
                        current = false;
                    }
                }
                else if (radius < shape.SolidLineBelowRadius)
                {
                    current = true;
                }

                solid[i] = current;
            }

            return solid;
        }

        /// <summary>
        /// Expands the sections into vertex rows, doubling up wherever the marking variant changes.
        ///
        /// Without the doubling, the quad spanning a change would interpolate u from one half of the
        /// atlas to the other and smear both variants along that stretch of road.
        /// </summary>
        private static void BuildRows(
            int sectionCount,
            float length,
            bool[] solidLine,
            out float[] rowDistance,
            out bool[] rowSolid,
            out bool[] rowStartsStrip)
        {
            var distances = new List<float>(sectionCount + 16);
            var solids = new List<bool>(sectionCount + 16);
            var starts = new List<bool>(sectionCount + 16);

            for (int i = 0; i < sectionCount; i++)
            {
                float distance = length * i / (sectionCount - 1);
                bool changed = i > 0 && solidLine[i] != solidLine[i - 1];

                if (changed)
                {
                    // Close the old variant at this position, then open the new one.
                    distances.Add(distance);
                    solids.Add(solidLine[i - 1]);
                    starts.Add(false);

                    distances.Add(distance);
                    solids.Add(solidLine[i]);
                    starts.Add(true);
                    continue;
                }

                distances.Add(distance);
                solids.Add(solidLine[i]);
                starts.Add(false);
            }

            rowDistance = distances.ToArray();
            rowSolid = solids.ToArray();
            rowStartsStrip = starts.ToArray();
        }

        /// <summary>Appends one cross-section: nine vertices, cambered, with its texture coordinates.</summary>
        private static void AppendRing(
            IRoadPath path,
            in RoadShape shape,
            float distance,
            bool solidLine,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs)
        {
            Vector3 center = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            Vector3 up = Vector3.Cross(path.GetDirectionAtDistance(distance), right).normalized;
            if (up.y < 0f)
            {
                up = -up;
            }

            center += up * shape.SurfaceLift;

            float half = shape.HalfWidth;
            float outer = shape.OuterHalfWidth;
            float quarter = half * 0.5f;

            float crownAtQuarter = shape.Crown * (1f - 0.25f);

            // Across the section, left to right. The asphalt edges appear twice.
            var across = new[]
            {
                -outer, -half, -half, -quarter, 0f, quarter, half, half, outer,
            };

            var rise = new[]
            {
                -shape.ShoulderDrop, 0f, 0f, crownAtQuarter, shape.Crown, crownAtQuarter, 0f, 0f,
                -shape.ShoulderDrop,
            };

            // Cross-slope at each vertex, used for the normals. The camber is a parabola, so its slope
            // is linear in the distance from the centre; the verge is a constant ramp.
            float shoulderSlope = shape.ShoulderWidth > 0.01f
                ? shape.ShoulderDrop / shape.ShoulderWidth
                : 0f;

            var slope = new[]
            {
                shoulderSlope,
                shoulderSlope,
                CrownSlope(-half, half, shape.Crown),
                CrownSlope(-quarter, half, shape.Crown),
                0f,
                CrownSlope(quarter, half, shape.Crown),
                CrownSlope(half, half, shape.Crown),
                -shoulderSlope,
                -shoulderSlope,
            };

            float variantU = solidLine ? SolidVariantU : DashedVariantU;
            float surfaceV = distance / Mathf.Max(0.5f, shape.Markings.CycleLength);
            float shoulderV = distance / ShoulderTextureScale;

            for (int k = 0; k < RingVertexCount; k++)
            {
                vertices.Add(center + right * across[k] + up * rise[k]);

                // Perpendicular to both the road direction and the tilted cross-section.
                normals.Add((up - right * slope[k]).normalized);

                bool isShoulderVertex = k <= 1 || k >= 7;
                if (isShoulderVertex)
                {
                    uvs.Add(new Vector2(across[k] / ShoulderTextureScale, shoulderV));
                }
                else
                {
                    // Normalised across the asphalt only, then folded into this variant's half of the
                    // atlas. Because it never involves the shoulder width, widening the verge cannot
                    // knock the markings out of alignment.
                    float s = (across[k] + half) / (2f * half);
                    uvs.Add(new Vector2(variantU + s * VariantWidthU, surfaceV));
                }
            }
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

            // The shelf sits at the height of the outer edge of the verge, not at the centreline. On the
            // centreline the coarse grid would interpolate above the climbing, curving carriageway and
            // push terrain up through the asphalt.
            float roadLevel = roadPoint.y - shape.RoadShelfDrop;
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
