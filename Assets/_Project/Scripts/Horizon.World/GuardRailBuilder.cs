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
        /// How far either side of a bridge to leave to the parapet, metres. Shorter than
        /// <see cref="PortalClearance"/> because an abutment is a much more local disturbance than a
        /// tunnel mouth — the ground is back to normal within a few posts.
        /// </summary>
        private const float BridgeClearance = 8f;

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
                //
                // Nothing on a bridge either, for a different reason: the drop is real and very much
                // wants a barrier, but the bridge builds its own parapet along that exact line and two
                // structures in one place is one too many. The margin covers the abutments, where the
                // ground is still climbing to meet the deck and a post placed by the drop test lands in
                // the gap between them.
                bool covered = course != null
                               && (course.IsCoveredOrNear(distance, PortalClearance)
                                   || course.IsBridged(distance, BridgeClearance));

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

        /// <summary>
        /// The barrier down the middle of a divided road: one continuous run along the median line, with
        /// no drop test at all.
        ///
        /// <para>Separate from <see cref="Build"/> rather than a flag on it, because the two answer
        /// opposite questions. A verge rail exists where the ground falls away and is absent where it
        /// does not, which is what makes it read as a response to the terrain. A median barrier exists
        /// because there is oncoming traffic on the other side of it, and that is true for every metre of
        /// the road regardless of what the ground is doing — a median that came and went with the
        /// embankment would be a bug wearing the costume of a feature.</para>
        ///
        /// <para>Runs along the centreline the two carriageways were offset from, so it needs no width of
        /// its own: the gap it stands in is whatever those offsets left.</para>
        /// </summary>
        /// <summary>
        /// Post spacing down the median, metres.
        ///
        /// <para>Three times <see cref="PostSpacing"/>, and the reason is arithmetic rather than taste. A
        /// verge rail exists in short runs on the exposed outside of a corner, so four-metre posts cost a
        /// couple of hundred triangles over a whole pass. A median runs the entire length of the road
        /// without a break — at four metres that was 2129 posts and 32 000 triangles of always-resident
        /// mesh, more than the entire town's street network, for a barrier seen edge-on at 130 km/h. At
        /// twelve it is a third of that and reads identically from a car.</para>
        /// </summary>
        private const float MedianPostSpacing = 12f;

        public static Mesh BuildMedian(
            IRoadPath centre,
            in RoadShape roadShape,
            RoadCourse course,
            string meshName = "MedianBarrierMesh")
        {
            float length = centre.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / MedianPostSpacing) + 1);

            var vertices = new List<Vector3>(4096);
            var normals = new List<Vector3>(4096);
            var uvs = new List<Vector2>(4096);
            var triangles = new List<int>(8192);

            var present = new bool[steps];
            var anchors = new Vector3[steps];
            var ups = new Vector3[steps];

            for (int step = 0; step < steps; step++)
            {
                float distance = length * step / (steps - 1);

                Vector3 right = centre.GetBankedRightAtDistance(
                    distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

                Vector3 up = Vector3.Cross(centre.GetDirectionAtDistance(distance), right).normalized;
                ups[step] = up.y < 0f ? -up : up;

                anchors[step] = centre.GetPositionAtDistance(distance) - ups[step] * roadShape.ShoulderDrop;

                // Nothing breaks the run, tunnels included. The motorway's bores are single spans over
                // both carriageways rather than one each, so inside one there is still oncoming traffic
                // a few metres away and still a reason for a barrier between it and you.
                present[step] = true;
            }

            for (int step = 0; step < steps; step++)
            {
                if (!present[step])
                {
                    continue;
                }

                AddPost(anchors[step], ups[step], vertices, normals, uvs, triangles);

                if (step + 1 < steps && present[step + 1])
                {
                    AddBeam(anchors[step], anchors[step + 1], ups[step], ups[step + 1],
                        vertices, normals, uvs, triangles);
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
