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

        /// <summary>
        /// Clearance between the edge of the verge and the rail.
        ///
        /// <para>Scaled with the verge. The post and beam heights above it deliberately were not: a
        /// guard rail's height is answerable to the car behind it, not to the width of the road.</para>
        /// </summary>
        private const float Standoff = 0.38f;

        /// <summary>How far either side of a portal to leave clear of rails, metres.</summary>
        private const float PortalClearance = 30f;

        /// <summary>
        /// How far either side of a bridge to leave to the parapet, metres. Shorter than
        /// <see cref="PortalClearance"/> because an abutment is a much more local disturbance than a
        /// tunnel mouth — the ground is back to normal within a few posts.
        /// </summary>
        private const float BridgeClearance = 8f;

        /// <summary>
        /// How far either side of a filling station the verge is left bare, metres.
        ///
        /// <para>A forecourt's frontage is open by construction — that is how a car gets onto it — so a
        /// line of posts across the entrance is a line of posts through the entrance. 45 covers the
        /// apron's 26 m half-length with enough either side for the taper.</para>
        ///
        /// <para>It takes out both verges rather than only the station's own. Threading a side through
        /// this walk would mean a side on every call that reaches it, for the sake of a rail on the far
        /// shoulder of a stretch that was chosen in the first place for being straight and level — which
        /// is to say, for being somewhere there is nothing to fall off.</para>
        /// </summary>
        private const float ForecourtClearance = 45f;

        /// <summary>
        /// How far either side of a fork the verge is left bare, metres.
        ///
        /// <para>Wider than a forecourt's 45, and the extra is the nose. A filling station's frontage is
        /// a gap in the verge; a fork's is a paved throat that keeps widening for as long as the two
        /// carriageways are converging, so the rail has to stop before the branch's own shoulder reaches
        /// this one. Sixty covers the throat <c>TrunkForkBuilder</c> lays with room for the taper.</para>
        /// </summary>
        private const float JunctionClearance = 60f;

        /// <summary>
        /// How far short of each end of the median the barrier stops, metres.
        ///
        /// <para><b>It used to stop nowhere at all</b> — <c>present[step]</c> was the literal
        /// <c>true</c>, with a comment saying that not even a bore breaks the run. That is right in the
        /// middle of a motorway and wrong at the two places it ends: the last post stood on Hochstadt's
        /// city gate and the first one on the mouth of the coast road, both of which begin on the median
        /// line. A solid wall across the only way in or out of a city is not a barrier, it is a wall.</para>
        ///
        /// <para>Sized against <c>MotorwayTerminusBuilder.TerminusLength</c>: the barrier has to be gone
        /// before the paving that brings the two carriageways together begins, or the terminus is an
        /// apron with a fence down the middle of it.</para>
        /// </summary>
        public const float MedianEndClearance = 240f;


        /// <summary>
        /// Builds every rail on the course as one mesh. Returns null when nothing is exposed enough to
        /// need one.
        ///
        /// One mesh rather than one per terrain tile: the whole set is a few thousand triangles, so a
        /// single draw call is cheaper than the bookkeeping of streaming it. Worth revisiting only if the
        /// world grows far beyond one pass.
        /// </summary>
        /// <summary>
        /// Where a rail stands, walked once and used twice.
        ///
        /// <para>Pulled out of <see cref="Build"/> when <see cref="BuildCollision"/> arrived, and that is
        /// the whole reason it exists: what the driver sees and what the car hits are different meshes,
        /// and there must not be two opinions about which stretches of verge are exposed. A wall in a
        /// place with no rail is worse than either.</para>
        /// </summary>
        private static void Plan(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            out bool[,] needed,
            out Vector3[,] anchors,
            out Vector3[] ups,
            out int steps)
        {
            float length = path.Length;
            steps = Mathf.Max(2, Mathf.CeilToInt(length / PostSpacing) + 1);

            needed = new bool[steps, 2];
            anchors = new Vector3[steps, 2];
            ups = new Vector3[steps];

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
                // And nothing across the mouth of a fork, for the reason a forecourt gets the same
                // treatment: the ground beside a junction does fall away and the drop test is right
                // about that, but a rail there stands across the road the branch exists to reach.
                bool covered = course != null
                               && (course.IsCoveredOrNear(distance, PortalClearance)
                                   || course.IsBridged(distance, BridgeClearance)
                                   || course.IsForecourt(distance, ForecourtClearance)
                                   || course.IsJunction(distance, JunctionClearance));

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
        }

        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            string meshName = "GuardRailMesh")
        {
            Plan(path, roadShape, field, course,
                out bool[,] needed, out Vector3[,] anchors, out Vector3[] ups, out int steps);

            var vertices = new List<Vector3>(2048);
            var normals = new List<Vector3>(2048);
            var uvs = new List<Vector2>(2048);
            var triangles = new List<int>(4096);

            // Second pass over the same walk, so a rail can be drawn between consecutive posts only
            // where both ends want one.
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

            return ToMesh(meshName, vertices, normals, uvs, triangles);
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
            float endClearance = 0f,
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

                // Nothing breaks the run in the middle, tunnels included. The motorway's bores are
                // single spans over both carriageways rather than one each, so inside one there is still
                // oncoming traffic a few metres away and still a reason for a barrier between it and you.
                //
                // The two ends are the exception, and the only one — see MedianEndClearance.
                present[step] = distance > endClearance && distance < length - endClearance;
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

            return ToMesh(meshName, vertices, normals, uvs, triangles);
        }

        /// <summary>
        /// Height of the wall the car actually hits, metres.
        ///
        /// <para>Just under the post, so the barrier never stands proud of the thing it is standing in.
        /// It is not the beam's height: a wall that started at the beam would let a wheel through
        /// underneath and drop the car off the mountain between two posts, which is the failure this
        /// whole mesh exists to stop.</para>
        /// </summary>
        private const float BarrierHeight = 0.75f;

        /// <summary>Thickness of that wall. Wide enough that a fast glancing hit cannot tunnel it.</summary>
        private const float BarrierThickness = 0.25f;

        /// <summary>
        /// How many posts a single collision segment spans.
        ///
        /// <para>Six, so a cross-section every 24 m. What is being approximated is a straight wall and
        /// the chord error on the tightest hairpin here is a couple of centimetres, against six times
        /// fewer triangles for PhysX to cook and keep resident along every exposed verge in the
        /// world.</para>
        /// </summary>
        private const int BarrierPostsPerSegment = 6;

        /// <summary>
        /// The guard rails as the car meets them: a smooth wall along the same line, with no posts in it.
        ///
        /// <para><b>Deliberately not the mesh above.</b> A <c>MeshCollider</c> taken from the rail as
        /// drawn is a row of re-entrant corners every four metres, and a car sliding along it catches on
        /// each one — which is what the old "no collider at all" decision was really objecting to. It
        /// also meant nothing at the edge of any road in the world was solid, so the rails were a
        /// picture of a barrier and the drop behind them was open. A plain wall in the same place
        /// answers both: the car is held and slides off, and the posts stay a picture.</para>
        ///
        /// <para>Runs only where <see cref="Plan"/> says a rail stands, from the same walk, because a
        /// wall somewhere there is visibly nothing is worse than no wall at all.</para>
        /// </summary>
        public static Mesh BuildCollision(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            string meshName = "GuardRailCollisionMesh")
        {
            Plan(path, roadShape, field, course,
                out bool[,] needed, out Vector3[,] anchors, out Vector3[] ups, out int steps);

            var vertices = new List<Vector3>(2048);
            var normals = new List<Vector3>(2048);
            var uvs = new List<Vector2>(2048);
            var triangles = new List<int>(4096);

            for (int side = 0; side < 2; side++)
            {
                int step = 0;
                while (step < steps)
                {
                    if (!needed[step, side])
                    {
                        step++;
                        continue;
                    }

                    // The whole unbroken run, then walled in segments across it — rather than one
                    // segment per post — so the wall has no joint where the rail has none.
                    int end = step;
                    while (end + 1 < steps && needed[end + 1, side])
                    {
                        end++;
                    }

                    for (int from = step; from < end; from += BarrierPostsPerSegment)
                    {
                        int to = Mathf.Min(end, from + BarrierPostsPerSegment);
                        AddWall(anchors[from, side], anchors[to, side], ups[from], ups[to],
                            vertices, normals, uvs, triangles);
                    }

                    step = end + 1;
                }
            }

            return ToMesh(meshName, vertices, normals, uvs, triangles);
        }

        /// <summary>The median barrier as the car meets it. See <see cref="BuildCollision"/>.</summary>
        public static Mesh BuildMedianCollision(
            IRoadPath centre,
            in RoadShape roadShape,
            RoadCourse course,
            float endClearance = 0f,
            string meshName = "MedianBarrierCollisionMesh")
        {
            float length = centre.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / (MedianPostSpacing * 2f)) + 1);

            var vertices = new List<Vector3>(4096);
            var normals = new List<Vector3>(4096);
            var uvs = new List<Vector2>(4096);
            var triangles = new List<int>(8192);

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
            }

            for (int step = 0; step + 1 < steps; step++)
            {
                // The same gap the drawn barrier has. What you can see and what you can hit are allowed
                // to differ here, but not about whether there is a wall.
                float distance = length * step / (steps - 1);
                float next = length * (step + 1) / (steps - 1);

                if (distance < endClearance || next > length - endClearance)
                {
                    continue;
                }

                AddWall(anchors[step], anchors[step + 1], ups[step], ups[step + 1],
                    vertices, normals, uvs, triangles);
            }

            return ToMesh(meshName, vertices, normals, uvs, triangles);
        }

        /// <summary>One closed length of wall, standing on the verge and squared to the road.</summary>
        private static void AddWall(
            Vector3 fromAnchor,
            Vector3 toAnchor,
            Vector3 fromUp,
            Vector3 toUp,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
            Vector3 along = toAnchor - fromAnchor;
            if (along.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector3 up = (fromUp + toUp).normalized;
            Vector3 side = Vector3.Cross(along.normalized, up).normalized * BarrierThickness;

            AddRectangularTube(fromAnchor, toAnchor, side, up * BarrierHeight,
                vertices, normals, uvs, triangles);
        }

        private static Mesh ToMesh(
            string meshName,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
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
