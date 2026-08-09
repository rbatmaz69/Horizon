using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.World
{
    /// <summary>
    /// Builds a tunnel as a **closed body of rock with a bore through it**, standing on untouched terrain.
    ///
    /// This replaces an approach that cut a slot in the height field and then tried to close it again with
    /// separately computed lids, portal walls and flank walls. That never converged, and it is worth saying
    /// why: a height field is quantised to its cell size — 12 m here — while the bore is about 10 m wide and
    /// 7 m tall, so the hole was always a different shape from the geometry meant to plug it. Every fix moved
    /// the mismatch somewhere else. Slabs jutting out of the hillside, triangular gaps showing sky, and in
    /// the end the road blocked again.
    ///
    /// So nothing is cut any more. The terrain stays one continuous surface, which removes that whole class
    /// of bug. What gets built is a mound whose outer skin, inner bore and two portal rings all come from a
    /// single parameterisation, and which is therefore watertight by construction rather than by adjustment.
    /// Its feet are buried below the ground, so there is no visible bottom edge and no floor to build.
    ///
    /// A gallery is the same body with the valley-side wall left open and pillars in its place.
    /// </summary>
    public static class TunnelBuilder
    {
        /// <summary>Submesh for the rock, inside and out.</summary>
        public const int RockSubmesh = 0;

        /// <summary>Submesh for the portal rings and pillars.</summary>
        public const int PortalSubmesh = 1;

        public const int TunnelSubmeshCount = 2;

        /// <summary>Points around the arch and around the massif. Shared, so the two can be stitched.</summary>
        private const int ArchSegments = 15;

        /// <summary>
        /// Spacing of the rings along the bore, metres.
        ///
        /// 12, matching <c>TerrainShape.CellSize</c>. Once the body is flat-shaded the ring spacing *is* its
        /// facet size, and at 5 m the facets were finer than the hillside's — which reads as corrugation, a
        /// ribbed caterpillar lying on the mountain, rather than as rock. Cutting the massif at the same
        /// pitch as the terrain is what makes the two look like the same material.
        ///
        /// Safe only because both covered stretches bracket a straight; a bore following a curve would need
        /// its rings closer together than its facets ideally are.
        /// </summary>
        private const float RingSpacing = 12f;

        /// <summary>How far the massif runs past each portal before its flanks settle into the ground.</summary>
        private const float EndOverhang = 24f;

        /// <summary>
        /// Half-width of the massif at its widest, metres.
        ///
        /// This is the number that decides whether the thing reads as a mountain or as a lump. A tunnel is
        /// built because a mountain is in the way, so the body has to be a mountain, not the seventeen-metre
        /// casing this started as. Because the body is closed it can be any size without risking a hole —
        /// the size is purely an artistic choice.
        ///
        /// 40, down from 52. On a switchback, neighbouring legs are only 40–80 m apart in plan, and at 52 m
        /// the body swallowed the leg below it whole. 40 keeps it a spur the road passes rather than a dome
        /// the road disappears under.
        /// </summary>
        private const float MoundHalfWidth = 40f;

        /// <summary>Height of the massif over the carriageway at its highest, metres.</summary>
        private const float MoundHeight = 26f;

        /// <summary>How much the massif narrows towards its ends, as a fraction of full size.</summary>
        private const float EndTaper = 0.6f;

        /// <summary>How far the feet are pushed below the ground so no edge shows.</summary>
        private const float FootBurial = 2f;

        /// <summary>Builds one covered stretch, or null when the span is too short to be worth building.</summary>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            RoadFeature feature,
            MountainField field,
            string meshName)
        {
            return Build(path, roadShape, feature, field, meshName, includeSkin: true);
        }

        /// <summary>
        /// The same body with only the surfaces a car should be able to hit: the bore lining, the portal
        /// rings and a gallery's pillars.
        ///
        /// The outer skin is deliberately left out of collision. It contributes nothing — the terrain is
        /// never cut for a tunnel, so the ground under the massif is already solid and already has its own
        /// collider — while adding a large concave surface right beside the carriageway for the car to catch
        /// on. That is not hypothetical: with the skin's feet pinned at road level, the descent gallery's
        /// massif came up through the road and stopped the car dead.
        ///
        /// Leaving it out means driving off the road into the flank of the massif clips through rock rather
        /// than stopping. That is the better failure: it is off the racing line, and it is visible.
        /// </summary>
        public static Mesh BuildCollision(
            IRoadPath path,
            in RoadShape roadShape,
            RoadFeature feature,
            MountainField field,
            string meshName)
        {
            return Build(path, roadShape, feature, field, meshName, includeSkin: false);
        }

        private static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            RoadFeature feature,
            MountainField field,
            string meshName,
            bool includeSkin)
        {
            if (feature.Length < RingSpacing * 2f)
            {
                return null;
            }

            bool gallery = feature.Kind == RoadFeatureKind.Gallery;

            // A real two-lane road tunnel: about 10 m across, 7 m to the crown.
            float archHalfWidth = roadShape.HalfWidth + 0.7f;
            float springLine = 2.3f;
            float crown = 6.9f;

            float startDistance = feature.StartDistance - EndOverhang;
            float endDistance = feature.EndDistance + EndOverhang;

            int rings = Mathf.Max(4, Mathf.CeilToInt((endDistance - startDistance) / RingSpacing) + 1);
            int stride = ArchSegments * 2;

            // The ring lattice is built as bare positions first, and the triangles are then emitted from it
            // with their own vertices. That is what makes the body flat-shaded, like every other mesh in the
            // world. It used to share vertices between rings and carry analytic smooth normals, and the
            // result was the only smooth-shaded object in a faceted landscape — a body the size of a hill
            // that read as a rubber lump laid over the mountain rather than as part of it.
            var points = new Vector3[rings * stride];
            var pointUvs = new Vector2[rings * stride];

            for (int ring = 0; ring < rings; ring++)
            {
                float t = ring / (float)(rings - 1);
                float distance = Mathf.Lerp(startDistance, endDistance, t);

                Vector3 center = path.GetPositionAtDistance(distance);
                Vector3 right = path.GetRightAtDistance(distance);

                // Tapered ends, so the mound settles into the hillside instead of stopping like a pipe.
                float taper = Mathf.Lerp(EndTaper, 1f, Mathf.Sin(Mathf.PI * Mathf.Clamp01(t)));

                // Variation along the length, or a body this size reads as an extruded dome.
                float wobble = 1f + 0.10f * Mathf.Sin(distance * 0.07f) + 0.05f * Mathf.Sin(distance * 0.19f);

                float moundHalf = MoundHalfWidth * taper * wobble;
                float moundTop = MoundHeight * taper * wobble;

                int ringStart = ring * stride;

                // Inner half of the stride first, then outer, so a portal ring can index both from one offset.
                for (int i = 0; i < ArchSegments; i++)
                {
                    float u = i / (float)(ArchSegments - 1);

                    points[ringStart + i] =
                        ArchPoint(center, right, u, archHalfWidth, springLine, crown, gallery);
                    pointUvs[ringStart + i] = new Vector2(u * 2f, distance * 0.1f);

                    points[ringStart + ArchSegments + i] =
                        MoundPoint(center, right, u, moundHalf, moundTop, field);
                    pointUvs[ringStart + ArchSegments + i] = new Vector2(u * 3f, distance * 0.06f);
                }
            }

            var vertices = new List<Vector3>(rings * stride * 6);
            var normals = new List<Vector3>(rings * stride * 6);
            var uvs = new List<Vector2>(rings * stride * 6);
            var rock = new List<int>(rings * ArchSegments * 12);
            var portal = new List<int>(ArchSegments * 12);

            for (int ring = 0; ring < rings - 1; ring++)
            {
                int here = ring * stride;
                int ahead = here + stride;

                for (int i = 0; i < ArchSegments - 1; i++)
                {
                    // Bore lining faces inwards — the only side it is ever seen from. The outer skin is the
                    // same stitch with the opposite winding. Unity culls by winding, not by the normal that
                    // gets assigned, which is what made an earlier version of this invisible from inside.
                    AddFlatQuad(vertices, normals, uvs, rock, points, pointUvs,
                        here + i, here + i + 1, ahead + i, ahead + i + 1, reverse: false);

                    if (!includeSkin)
                    {
                        continue;
                    }

                    int outerHere = here + ArchSegments;
                    int outerAhead = ahead + ArchSegments;
                    AddFlatQuad(vertices, normals, uvs, rock, points, pointUvs,
                        outerHere + i, outerHere + i + 1, outerAhead + i, outerAhead + i + 1, reverse: true);
                }
            }

            // Portal rings: the annulus between bore and skin at each end. Both boundaries come from the same
            // parameter, so this closes the body exactly — no gap to measure, no wall to size.
            AddPortalRing(points, vertices, normals, uvs, portal, 0, false);
            AddPortalRing(points, vertices, normals, uvs, portal, (rings - 1) * stride, true);

            if (gallery)
            {
                AddPillars(path, feature, archHalfWidth, springLine, vertices, normals, uvs, portal);
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = TunnelSubmeshCount;
            mesh.SetTriangles(rock, RockSubmesh);
            mesh.SetTriangles(portal, PortalSubmesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A point on the bore outline: straight walls to the spring line, half-round above it.</summary>
        private static Vector3 ArchPoint(
            Vector3 center,
            Vector3 right,
            float u,
            float halfWidth,
            float springLine,
            float crown,
            bool gallery)
        {
            float angle = Mathf.PI * u;
            float across = -Mathf.Cos(angle) * halfWidth;
            float above = springLine + Mathf.Sin(angle) * (crown - springLine);

            if (u < 0.05f || u > 0.95f)
            {
                across = Mathf.Sign(across) * halfWidth;
                above = 0f;
            }

            // The open side of a gallery: the wall stops at the roof edge.
            if (gallery && across > halfWidth * 0.5f && above < springLine)
            {
                above = springLine;
            }

            return center + right * across + Vector3.up * above;
        }

        /// <summary>
        /// A point on the outer skin: a half-ellipse over the road, with its feet buried in the ground so the
        /// body needs no floor and shows no bottom edge.
        ///
        /// The burial used to be written as <c>Mathf.Max(center.y + above, buried)</c>, and it never once
        /// took effect: <c>above</c> is a sine over [0, π] scaled by a positive roughness factor, so it is
        /// never negative, so the maximum was always the skin. The body therefore stood on the plane of the
        /// *covered* road rather than in the hillside — a flat rim at road level, 52 m out to each side. On a
        /// switchback that rim sweeps straight over the neighbouring leg: hanging rock where the tunnel road
        /// is higher than its neighbour, and rock coming up through the carriageway where it is lower.
        ///
        /// So the feet are now pulled down explicitly instead of being clamped from below. The outer third of
        /// the arc dives under the terrain, which is what the class comment claimed all along.
        /// </summary>
        private static Vector3 MoundPoint(
            Vector3 center,
            Vector3 right,
            float u,
            float halfWidth,
            float height,
            MountainField field)
        {
            float angle = Mathf.PI * u;
            float across = -Mathf.Cos(angle) * halfWidth;
            float lift = Mathf.Sin(angle);

            Vector3 point = center + right * across;

            // Break up the silhouette so the flanks look like rock rather than a swept ellipse. Applied
            // before the portal rings are built, and they read the finished vertices — so roughening the
            // skin cannot open the body up.
            float roughness = Mathf.PerlinNoise(point.x * 0.02f, point.z * 0.02f) - 0.5f;

            float skin = center.y + lift * height * (1f + roughness * 0.34f);

            float ground = field != null ? field.HeightAt(point.x, point.z) : center.y;
            float foot = Mathf.Min(center.y, ground) - FootBurial;

            // Reaches the ellipse by the time the arch is half up, so only the skirt is spent descending.
            float y = Mathf.Lerp(foot, skin, Mathf.Clamp01(lift * 2f));

            return new Vector3(point.x, y, point.z);
        }

        /// <summary>
        /// Closes one end by filling between the bore outline and the skin outline, both taken at the same
        /// parameter so the ring is watertight.
        /// </summary>
        private static void AddPortalRing(
            Vector3[] points,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles,
            int ringStart,
            bool facingForward)
        {
            int innerStart = ringStart;
            int outerStart = ringStart + ArchSegments;

            // One normal for the whole face, so the rock does not appear to bend round the corner at the
            // mouth. Averaged from the geometry rather than taken from the path direction, which keeps it
            // correct whatever the road is doing there.
            Vector3 faceNormal = Vector3.zero;
            for (int i = 0; i < ArchSegments - 1; i++)
            {
                Vector3 a = points[innerStart + i];
                Vector3 b = points[innerStart + i + 1];
                Vector3 c = points[outerStart + i];
                faceNormal += Vector3.Cross(b - a, c - a);
            }

            faceNormal = faceNormal.sqrMagnitude > 0.0001f ? faceNormal.normalized : Vector3.forward;
            if (!facingForward)
            {
                faceNormal = -faceNormal;
            }

            for (int i = 0; i < ArchSegments - 1; i++)
            {
                Vector3 inner0 = points[innerStart + i];
                Vector3 inner1 = points[innerStart + i + 1];
                Vector3 outer1 = points[outerStart + i + 1];
                Vector3 outer0 = points[outerStart + i];

                int baseIndex = vertices.Count;
                vertices.Add(inner0);
                vertices.Add(inner1);
                vertices.Add(outer1);
                vertices.Add(outer0);

                for (int v = 0; v < 4; v++)
                {
                    normals.Add(faceNormal);
                    uvs.Add(new Vector2(v & 1, (v >> 1) & 1));
                }

                bool flip = Vector3.Dot(Vector3.Cross(inner1 - inner0, outer1 - inner0), faceNormal) < 0f;
                AddFan(triangles, baseIndex, flip);
            }
        }

        /// <summary>
        /// Two flat-shaded triangles across a quad of the ring lattice, wound one way or the other.
        ///
        /// Every triangle gets its own three vertices so it can keep a single face normal. That costs six
        /// vertices per quad instead of sharing four, which for a body of this size is a few thousand
        /// vertices — nothing against making it read as the same kind of rock as the terrain.
        /// </summary>
        private static void AddFlatQuad(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles,
            Vector3[] points,
            Vector2[] pointUvs,
            int a,
            int b,
            int c,
            int d,
            bool reverse)
        {
            if (reverse)
            {
                AddFlatTriangle(vertices, normals, uvs, triangles, points, pointUvs, a, c, b);
                AddFlatTriangle(vertices, normals, uvs, triangles, points, pointUvs, b, c, d);
            }
            else
            {
                AddFlatTriangle(vertices, normals, uvs, triangles, points, pointUvs, a, b, c);
                AddFlatTriangle(vertices, normals, uvs, triangles, points, pointUvs, b, d, c);
            }
        }

        private static void AddFlatTriangle(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles,
            Vector3[] points,
            Vector2[] pointUvs,
            int a,
            int b,
            int c)
        {
            Vector3 pa = points[a];
            Vector3 pb = points[b];
            Vector3 pc = points[c];

            Vector3 normal = Vector3.Cross(pb - pa, pc - pa);
            if (normal.sqrMagnitude < 0.000001f)
            {
                // Degenerate. The arch outline collapses to a point at the springing, so this is expected
                // rather than exceptional.
                return;
            }

            normal.Normalize();

            int baseIndex = vertices.Count;
            vertices.Add(pa);
            vertices.Add(pb);
            vertices.Add(pc);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            uvs.Add(pointUvs[a]);
            uvs.Add(pointUvs[b]);
            uvs.Add(pointUvs[c]);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }

        /// <summary>Two triangles across four consecutive vertices.</summary>
        private static void AddFan(List<int> triangles, int baseIndex, bool flip)
        {
            if (flip)
            {
                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 1);

                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 3);
                triangles.Add(baseIndex + 2);
            }
            else
            {
                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);

                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);
            }
        }

        /// <summary>Pillars along the open side of a gallery, holding the roof up.</summary>
        private static void AddPillars(
            IRoadPath path,
            RoadFeature feature,
            float halfWidth,
            float springLine,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
            const float spacing = 12f;
            const float thickness = 0.55f;

            int count = Mathf.Max(2, Mathf.FloorToInt(feature.Length / spacing));

            for (int i = 0; i <= count; i++)
            {
                float distance = Mathf.Lerp(feature.StartDistance, feature.EndDistance, i / (float)count);
                Vector3 center = path.GetPositionAtDistance(distance);
                Vector3 right = path.GetRightAtDistance(distance);
                Vector3 forward = path.GetDirectionAtDistance(distance);

                Vector3 foot = center + right * (halfWidth - thickness * 0.5f);
                AddBox(foot, right * thickness, forward * thickness, Vector3.up * (springLine + 1.4f),
                    vertices, normals, uvs, triangles);
            }
        }

        private static void AddBox(
            Vector3 origin,
            Vector3 across,
            Vector3 along,
            Vector3 up,
            List<Vector3> vertices,
            List<Vector3> normals,
            List<Vector2> uvs,
            List<int> triangles)
        {
            Vector3 a = origin - across * 0.5f - along * 0.5f;
            Vector3[] bottom = { a, a + across, a + across + along, a + along };

            for (int i = 0; i < 4; i++)
            {
                Vector3 p0 = bottom[i];
                Vector3 p1 = bottom[(i + 1) % 4];
                Vector3 p2 = p1 + up;
                Vector3 p3 = p0 + up;

                Vector3 normal = Vector3.Cross(p1 - p0, p3 - p0).normalized;

                int baseIndex = vertices.Count;
                vertices.Add(p0);
                vertices.Add(p1);
                vertices.Add(p2);
                vertices.Add(p3);

                for (int v = 0; v < 4; v++)
                {
                    normals.Add(normal);
                    uvs.Add(new Vector2(v & 1, (v >> 1) & 1));
                }

                AddFan(triangles, baseIndex, false);
            }
        }
    }
}
