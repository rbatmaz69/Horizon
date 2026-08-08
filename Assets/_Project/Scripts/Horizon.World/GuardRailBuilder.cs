using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>
    /// Puts guard rails where the ground actually falls away.
    ///
    /// The test is against the terrain height a few metres beyond the verge, not against curvature or a
    /// hand-placed list — so rails appear on the exposed outer edge of a hairpin and are absent where the
    /// same corner runs through a cutting. That is what makes them read as a response to the drop rather
    /// than as decoration, and it means reshaping the pass moves them automatically.
    /// </summary>
    public static class GuardRailBuilder
    {
        /// <summary>Spacing of the posts, metres.</summary>
        private const float PostSpacing = 4f;

        /// <summary>Drop beyond the verge that justifies a rail, metres.</summary>
        private const float DropThreshold = 3f;

        /// <summary>How far beyond the verge the drop is measured.</summary>
        private const float ProbeDistance = 7f;

        private const float PostWidth = 0.12f;
        private const float PostHeight = 0.80f;
        private const float BeamBottom = 0.44f;
        private const float BeamTop = 0.78f;
        private const float BeamThickness = 0.07f;

        /// <summary>Clearance between the edge of the verge and the rail.</summary>
        private const float Standoff = 0.3f;

        /// <summary>How far either side of a portal to leave clear of rails, metres.</summary>
        private const float PortalClearance = 30f;

        /// <summary>
        /// Builds every rail on the course as one mesh. Returns null when nothing is exposed enough to
        /// need one.
        ///
        /// One mesh rather than one per terrain tile: the whole set is a few thousand triangles, so a
        /// single draw call is cheaper than the bookkeeping of streaming it. Worth revisiting only if the
        /// world grows far beyond one pass.
        /// </summary>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            string meshName = "GuardRailMesh")
        {
            float length = path.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / PostSpacing) + 1);

            var vertices = new List<Vector3>(2048);
            var normals = new List<Vector3>(2048);
            var uvs = new List<Vector2>(2048);
            var triangles = new List<int>(4096);

            // Two passes over the same walk: decide first, build second, so a rail can be drawn between
            // consecutive posts only where both ends want one.
            var needed = new bool[steps, 2];
            var anchors = new Vector3[steps, 2];
            var ups = new Vector3[steps];

            for (int step = 0; step < steps; step++)
            {
                float distance = length * step / (steps - 1);

                Vector3 center = path.GetPositionAtDistance(distance);
                Vector3 right = path.GetBankedRightAtDistance(
                    distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

                Vector3 up = Vector3.Cross(path.GetDirectionAtDistance(distance), right).normalized;
                if (up.y < 0f)
                {
                    up = -up;
                }

                ups[step] = up;

                // Nothing inside a bore or near its mouth. Inside there is no drop to fall down and the
                // posts would foul the wall; at the mouth the ground has been cut away for the slot, so a
                // plain drop test reads a large drop and leaves a post standing in mid-air.
                bool covered = course != null && course.IsCoveredOrNear(distance, PortalClearance);

                for (int side = 0; side < 2; side++)
                {
                    float sign = side == 0 ? -1f : 1f;
                    float across = (roadShape.OuterHalfWidth + Standoff) * sign;

                    Vector3 anchor = center + right * across - up * roadShape.ShoulderDrop;
                    anchors[step, side] = anchor;

                    if (covered)
                    {
                        continue;
                    }

                    Vector3 probe = center + right * ((roadShape.OuterHalfWidth + ProbeDistance) * sign);
                    float groundHeight = field.HeightAt(probe.x, probe.z);

                    needed[step, side] = anchor.y - groundHeight > DropThreshold;
                }
            }

            for (int step = 0; step < steps; step++)
            {
                for (int side = 0; side < 2; side++)
                {
                    if (!needed[step, side])
                    {
                        continue;
                    }

                    Vector3 anchor = anchors[step, side];
                    Vector3 up = ups[step];

                    AddPost(anchor, up, vertices, normals, uvs, triangles);

                    // Beam only where the next post also exists, so a run ends cleanly.
                    if (step + 1 < steps && needed[step + 1, side])
                    {
                        AddBeam(anchor, anchors[step + 1, side], up, ups[step + 1],
                            vertices, normals, uvs, triangles);
                    }
                }
            }

            if (triangles.Count == 0)
            {
                return null;
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

        private static void AddPost(
            Vector3 anchor,
            Vector3 up,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
            // A post is a short square section standing on the verge; orientation across the road does
            // not matter at this size, so world axes are good enough.
            Vector3 side = Vector3.Cross(up, Vector3.forward);
            if (side.sqrMagnitude < 0.01f)
            {
                side = Vector3.right;
            }

            side = side.normalized * PostWidth;
            Vector3 along = Vector3.Cross(side.normalized, up).normalized * PostWidth;

            AddRectangularTube(anchor, anchor + up * PostHeight, side, along,
                vertices, normals, uvs, triangles);
        }

        private static void AddBeam(
            Vector3 fromAnchor,
            Vector3 toAnchor,
            Vector3 fromUp,
            Vector3 toUp,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
            Vector3 from = fromAnchor + fromUp * ((BeamBottom + BeamTop) * 0.5f);
            Vector3 to = toAnchor + toUp * ((BeamBottom + BeamTop) * 0.5f);

            Vector3 along = to - from;
            if (along.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector3 up = (fromUp + toUp).normalized;
            Vector3 side = Vector3.Cross(along.normalized, up).normalized * BeamThickness;
            Vector3 height = up * (BeamTop - BeamBottom);

            AddRectangularTube(from - height * 0.5f, to - height * 0.5f, side, height,
                vertices, normals, uvs, triangles);
        }

        /// <summary>
        /// Extrudes a rectangle from <paramref name="start"/> to <paramref name="end"/>. Used for both the
        /// posts and the beam, which differ only in proportion.
        /// </summary>
        private static void AddRectangularTube(
            Vector3 start,
            Vector3 end,
            Vector3 side,
            Vector3 height,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
            Vector3[] offsets =
            {
                -side * 0.5f,
                -side * 0.5f + height,
                side * 0.5f + height,
                side * 0.5f,
            };

            for (int i = 0; i < 4; i++)
            {
                Vector3 o0 = offsets[i];
                Vector3 o1 = offsets[(i + 1) % 4];

                Vector3 a = start + o0;
                Vector3 b = start + o1;
                Vector3 c = end + o1;
                Vector3 d = end + o0;

                Vector3 normal = Vector3.Cross(b - a, d - a).normalized;

                int baseIndex = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                vertices.Add(d);

                for (int v = 0; v < 4; v++)
                {
                    normals.Add(normal);
                    uvs.Add(new Vector2(v & 1, (v >> 1) & 1));
                }

                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);

                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);
            }
        }
    }
}
