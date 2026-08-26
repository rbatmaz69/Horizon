using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>
    /// Generates the carriageway from an <see cref="IRoadPath"/>: two marked lanes, a cambered surface
    /// and gravel shoulders.
    ///
    /// Meshes are built once at edit time and saved as assets — nothing here should run during play.
    /// The terrain that surrounds the road is not built here; that belongs to
    /// <see cref="TerrainTileBuilder"/> and <see cref="MountainField"/>.
    /// </summary>
    public static class RoadMeshBuilder
    {
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
            return BuildRoad(path, shape, meshName, 0f, path.Length);
        }

        /// <summary>
        /// The same carriageway, ending short of its own path.
        ///
        /// <para><b>What this is for.</b> A branch road's course runs all the way to the centreline of
        /// the road it leaves, because that is where <c>ConnectTo</c> was aimed and because
        /// <see cref="MountainField"/> wants the samples. Its <i>ribbon</i> must not: laid to the same
        /// length it puts its last cross-section — asphalt, gravel shoulder and the shoulder drop under
        /// it — across the carriageway of the road it is joining, ending in a square cap on the driving
        /// line. <see cref="TrunkForkBuilder"/> pays the trim back out as a throat.</para>
        ///
        /// <para><b>The rows keep their true arc length.</b> The marking variants are resolved over the
        /// whole path before the trim and the surviving rows are the same rows, at the same distances,
        /// as they would have been untrimmed — so the atlas keeps its phase and the hysteresis in
        /// <see cref="ResolveLineVariants"/> still starts where the road does. Re-sampling from the cut
        /// instead would dash differently on either side of every junction in the world. It is the same
        /// condition <c>StreetJunctionBuilder</c>'s <c>TrimStart</c>/<c>TrimEnd</c> satisfy.</para>
        /// </summary>
        public static Mesh BuildRoad(
            IRoadPath path,
            in RoadShape shape,
            string meshName,
            float fromDistance,
            float toDistance)
        {
            float length = path.Length;
            int sectionCount = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.5f, shape.StepLength)) + 1);

            bool[] solidLine = ResolveLineVariants(path, shape, sectionCount, length);

            // Rows of vertices. A change of marking variant emits the section twice — see the note on
            // BuildRows for why.
            BuildRows(sectionCount, length, solidLine, out float[] rowDistance, out bool[] rowSolid,
                out bool[] rowStartsStrip);

            TrimRows(Mathf.Max(0f, fromDistance), Mathf.Min(length, toDistance < 0f ? length : toDistance),
                ref rowDistance, ref rowSolid, ref rowStartsStrip);

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

        /// <summary>
        /// Drops the rows outside <paramref name="from"/>..<paramref name="to"/> and puts an exact row
        /// on each end.
        ///
        /// <para>The ends are placed rather than rounded to the nearest surviving row: a step is up to
        /// <c>RoadShape.StepLength</c> long, and the one place a ribbon's end is looked at closely is
        /// the mouth of a junction, where being four metres out is the difference between meeting the
        /// throat and leaving a gap in the road.</para>
        /// </summary>
        private static void TrimRows(
            float from,
            float to,
            ref float[] rowDistance,
            ref bool[] rowSolid,
            ref bool[] rowStartsStrip)
        {
            if (rowDistance.Length == 0 || (from <= 0f && to >= rowDistance[rowDistance.Length - 1]))
            {
                return;
            }

            var distances = new List<float>(rowDistance.Length);
            var solids = new List<bool>(rowDistance.Length);
            var starts = new List<bool>(rowDistance.Length);

            for (int i = 0; i < rowDistance.Length; i++)
            {
                if (rowDistance[i] < from || rowDistance[i] > to)
                {
                    continue;
                }

                distances.Add(rowDistance[i]);
                solids.Add(rowSolid[i]);
                starts.Add(rowStartsStrip[i]);
            }

            if (distances.Count == 0)
            {
                Debug.LogWarning($"[Horizon] A road ribbon was trimmed to {from:0.0}..{to:0.0} m and "
                                 + "nothing is left of it. That is a trim longer than the road.");
                rowDistance = System.Array.Empty<float>();
                rowSolid = System.Array.Empty<bool>();
                rowStartsStrip = System.Array.Empty<bool>();
                return;
            }

            // The end rows take the variant of the row they are extending rather than the one the
            // variant table says, so a cut that lands on a change does not open a strip with nothing
            // in it: the first row never carries a quad anyway, and the last would carry one across a
            // seam the atlas cannot cross.
            if (distances[0] > from + 0.01f)
            {
                distances.Insert(0, from);
                solids.Insert(0, solids[0]);
                starts.Insert(0, false);
            }

            starts[0] = false;

            if (distances[distances.Count - 1] < to - 0.01f)
            {
                distances.Add(to);
                solids.Add(solids[solids.Count - 1]);
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

            // The banked vector, not the level one. Everything else follows from it: the section is laid
            // out along this axis and the surface normal is derived from it, so the camber comes out of
            // one substitution rather than being applied as a correction afterwards.
            Vector3 right = path.GetBankedRightAtDistance(distance, shape.MaxBankDegrees, shape.FullBankRadius);

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
