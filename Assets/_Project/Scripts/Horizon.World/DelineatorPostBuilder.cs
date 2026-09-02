using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>
    /// Roadside delineator posts — the white posts with a reflector that line every real country road
    /// and mountain pass.
    ///
    /// <para><b>Why they are here.</b> Not decoration: they are the near-field reference the world was
    /// missing. Speed is read from things passing close by, and on open road there was nothing to read
    /// it against — the centre line cycles every twelve metres but sits flat on the surface, the
    /// vegetation is scattered rather than regular, and the guard rails only exist where the ground
    /// falls away. Between them the car could be doing 60 or 160 and the view out of the windscreen
    /// barely changed. A regularly spaced row of objects a couple of metres from the wheels is what the
    /// eye actually integrates motion from, and it costs one static draw call.</para>
    ///
    /// <para><b>Why they follow the guard rail's shape.</b> Same walk, same portal and bridge rules,
    /// same one-merged-mesh answer — see <see cref="GuardRailBuilder"/>, which worked all of that out
    /// first. The differences are the interesting part, and there are two.</para>
    /// </summary>
    public static class DelineatorPostBuilder
    {
        /// <summary>
        /// Spacing on a straight, metres. The real figure, and it is not arbitrary: fifty metres is
        /// close enough that one is always in view and far enough apart that they never read as fencing.
        /// </summary>
        private const float StraightSpacing = 50f;

        /// <summary>
        /// Spacing through a corner, metres. Real roads tighten the interval exactly here, to show the
        /// driver where the road goes — which happens to put the strongest speed cue at the moment the
        /// car is moving fastest relative to the road. Both readings want the same number.
        /// </summary>
        private const float CornerSpacing = 25f;

        /// <summary>Corner radius at or below which the spacing is fully tightened, metres.</summary>
        private const float TightRadius = 60f;

        /// <summary>Corner radius at or above which the road counts as straight, metres.</summary>
        private const float OpenRadius = 90f;

        /// <summary>Clearance between the edge of the verge and the post. Scaled with the verge.</summary>
        private const float Standoff = 0.56f;

        private const float PostHeight = 1f;
        private const float PostWidthBottom = 0.12f;
        private const float PostWidthTop = 0.10f;

        /// <summary>Height of the centre of the reflector above the base of the post.</summary>
        private const float ReflectorHeight = 0.84f;

        private const float ReflectorWidth = 0.07f;
        private const float ReflectorTall = 0.16f;

        /// <summary>How far the reflector stands proud of the post face, to keep it off the z-fight.</summary>
        private const float ReflectorProud = 0.004f;

        /// <summary>Drop beyond the verge that means a guard rail is already there.</summary>
        private const float RailDropThreshold = 3f;

        /// <summary>How far beyond the verge that drop is measured. Matches the guard rail's probe.</summary>
        private const float RailProbeDistance = 7f;

        /// <summary>How far either side of a portal to leave clear.</summary>
        private const float PortalClearance = 30f;

        /// <summary>How far either side of a bridge to leave to the parapet.</summary>
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
        /// How far either side of a fork the posts stop, metres. Matches
        /// <c>GuardRailBuilder.JunctionClearance</c>, because the two walk the same verge and a bare
        /// stretch of rail with posts still marching across it reads worse than either alone.
        /// </summary>
        private const float JunctionClearance = 60f;


        /// <summary>
        /// Builds every post on the course as one mesh with two submeshes — post body, then reflectors —
        /// so the reflector can carry its own material without a second draw of the geometry.
        ///
        /// <para>Returns null where the road is short enough or covered enough that nothing was placed,
        /// matching <see cref="GuardRailBuilder.Build"/> so the caller can report it the same way.</para>
        /// </summary>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            string meshName = "DelineatorMesh")
        {
            float length = path.Length;
            if (length <= 0f)
            {
                return null;
            }

            var vertices = new List<Vector3>(4096);
            var normals = new List<Vector3>(4096);
            var uvs = new List<Vector2>(4096);
            var postTriangles = new List<int>(6144);
            var reflectorTriangles = new List<int>(1024);

            // Walked with an accumulating cursor rather than a precomputed step count, because the
            // spacing is not constant — it is read from the curvature at each post, so where the next
            // one goes is only known once this one has been placed.
            float distance = 0f;

            while (distance <= length)
            {
                float radius = path.GetRadiusAtDistance(distance, 10f);
                float spacing = SpacingForRadius(radius);

                // A fork's mouth, on the same argument the forecourt makes and rather more strongly: a
                // post is only a marker, but a line of markers across a junction marks the wrong thing.
                bool covered = course != null
                               && (course.IsCoveredOrNear(distance, PortalClearance)
                                   || course.IsBridged(distance, BridgeClearance)
                                   || course.IsForecourt(distance, ForecourtClearance)
                                   || course.IsJunction(distance, JunctionClearance));

                if (!covered)
                {
                    Vector3 center = path.GetPositionAtDistance(distance);
                    Vector3 right = path.GetBankedRightAtDistance(
                        distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

                    Vector3 up = Vector3.Cross(path.GetDirectionAtDistance(distance), right).normalized;
                    if (up.y < 0f)
                    {
                        up = -up;
                    }

                    for (int side = 0; side < 2; side++)
                    {
                        float sign = side == 0 ? -1f : 1f;

                        // Where the ground falls away the guard rail is already standing on this line,
                        // and a real road puts its reflectors on the rail rather than a post in front of
                        // it. Same test and same numbers as GuardRailBuilder, so the two can never both
                        // claim a spot — if that threshold moves, it has to move in both places.
                        float across = (roadShape.OuterHalfWidth + Standoff) * sign;
                        Vector3 anchor = center + right * across - up * roadShape.ShoulderDrop;

                        if (field != null)
                        {
                            Vector3 probe = center
                                + right * ((roadShape.OuterHalfWidth + RailProbeDistance) * sign);

                            if (anchor.y - field.HeightAt(probe.x, probe.z) > RailDropThreshold)
                            {
                                continue;
                            }
                        }

                        AddPost(anchor, up, right, -sign,
                            vertices, normals, uvs, postTriangles, reflectorTriangles);
                    }
                }

                // A road that ends between two posts should still get one at the end, or the last
                // fifty metres read as though the road had stopped being a road.
                if (distance < length && distance + spacing > length)
                {
                    distance = length;
                }
                else
                {
                    distance += spacing;
                }
            }

            if (postTriangles.Count == 0)
            {
                return null;
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(postTriangles, 0);
            mesh.SetTriangles(reflectorTriangles, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Interpolates the spacing between the corner and straight figures.</summary>
        private static float SpacingForRadius(float radius)
        {
            float open = Mathf.Clamp01(Mathf.InverseLerp(TightRadius, OpenRadius, radius));
            return Mathf.Lerp(CornerSpacing, StraightSpacing, open);
        }

        /// <summary>
        /// One post: a tapered four-sided shaft with a cap, plus a reflector panel on the face turned
        /// toward the carriageway.
        ///
        /// <para><paramref name="inward"/> is the sign that points across the road from this post — the
        /// reflector goes on that face, because a reflector aimed at the hillside is a reflector nobody
        /// ever sees.</para>
        /// </summary>
        private static void AddPost(
            Vector3 anchor,
            Vector3 up,
            Vector3 right,
            float inward,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> postTriangles,
            List<int> reflectorTriangles)
        {
            Vector3 across = right * inward;
            Vector3 along = Vector3.Cross(up, across).normalized;

            Vector3 top = anchor + up * PostHeight;

            float halfBottom = PostWidthBottom * 0.5f;
            float halfTop = PostWidthTop * 0.5f;

            // Four corners at each end, wound the same way round so the sides pair up in order.
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 offsetBottom = CornerOffset(corner, across, along, halfBottom);
                Vector3 offsetTop = CornerOffset(corner, across, along, halfTop);

                int baseIndex = vertices.Count;

                Vector3 nextBottom = CornerOffset((corner + 1) % 4, across, along, halfBottom);
                Vector3 nextTop = CornerOffset((corner + 1) % 4, across, along, halfTop);

                Vector3 faceNormal = Vector3.Cross(
                    (nextBottom - offsetBottom).normalized, up).normalized;

                vertices.Add(anchor + offsetBottom);
                vertices.Add(anchor + nextBottom);
                vertices.Add(top + nextTop);
                vertices.Add(top + offsetTop);

                for (int i = 0; i < 4; i++)
                {
                    normals.Add(faceNormal);
                }

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));

                AddQuad(postTriangles, baseIndex);
            }

            // Cap, so the post is not hollow when looked down on from a hairpin above it.
            int capIndex = vertices.Count;
            for (int corner = 0; corner < 4; corner++)
            {
                vertices.Add(top + CornerOffset(corner, across, along, halfTop));
                normals.Add(up);
                uvs.Add(new Vector2(corner == 1 || corner == 2 ? 1f : 0f, corner >= 2 ? 1f : 0f));
            }

            AddQuad(postTriangles, capIndex);

            // Reflector, standing just proud of the road-facing shaft so the two never z-fight.
            Vector3 face = anchor + up * ReflectorHeight
                           + across * (halfTop + ReflectorProud);

            int reflectorIndex = vertices.Count;

            vertices.Add(face - along * (ReflectorWidth * 0.5f) - up * (ReflectorTall * 0.5f));
            vertices.Add(face + along * (ReflectorWidth * 0.5f) - up * (ReflectorTall * 0.5f));
            vertices.Add(face + along * (ReflectorWidth * 0.5f) + up * (ReflectorTall * 0.5f));
            vertices.Add(face - along * (ReflectorWidth * 0.5f) + up * (ReflectorTall * 0.5f));

            for (int i = 0; i < 4; i++)
            {
                normals.Add(across);
            }

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));

            AddQuad(reflectorTriangles, reflectorIndex);
        }

        /// <summary>Corner <paramref name="corner"/> of a square of half-width <paramref name="half"/>.</summary>
        private static Vector3 CornerOffset(int corner, Vector3 across, Vector3 along, float half)
        {
            float acrossSign = corner == 0 || corner == 3 ? -1f : 1f;
            float alongSign = corner <= 1 ? -1f : 1f;
            return across * (acrossSign * half) + along * (alongSign * half);
        }

        private static void AddQuad(List<int> triangles, int baseIndex)
        {
            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
        }
    }
}
