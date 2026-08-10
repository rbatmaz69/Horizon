using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Fills the gap where a village lane meets a road.
    ///
    /// Nothing else in the project builds junctions. <see cref="RoadMeshBuilder"/> emits a ribbon and
    /// leaves both ends as open edges — there is no cap, no skirt and nothing to attach to — so where a
    /// lane arrived at the main road the two surfaces simply stopped near each other and left a step you
    /// could see from the car.
    ///
    /// This is a throat: a quad strip stretched between the main road's outer edge and the lane's own
    /// cross-section a few metres in, so the surface is continuous from one carriageway into the other.
    /// It is deliberately not trying to be a *marked* junction. The road's markings live in a baked atlas
    /// whose v coordinate is arc length in each path's own frame, so two paths share no dash phase and
    /// there is no unpainted column in the texture to fall back on. Painting a junction is a texture
    /// problem, not a geometry one, and it is not this method's business.
    /// </summary>
    public static class VillageRoadBuilder
    {
        /// <summary>Matches RoadMeshBuilder's own submeshes, so the same material array works.</summary>
        public const int SurfaceSubmesh = 0;

        public const int SubmeshCount = 1;

        /// <summary>Steps across the throat. Enough to follow the main road's curve through the junction.</summary>
        private const int Steps = 8;

        /// <summary>
        /// One junction. <paramref name="throat"/> is how far back along the main road the mouth opens and
        /// how far along the lane the blend runs — the same number, which is what makes it look like a
        /// bell mouth rather than a wedge.
        /// </summary>
        public static Mesh Build(
            IRoadPath main,
            float alongMain,
            IRoadPath lane,
            in RoadShape mainShape,
            in RoadShape laneShape,
            float side,
            float throat,
            string meshName)
        {
            if (main == null || lane == null || throat < 1f)
            {
                return null;
            }

            var vertices = new List<Vector3>(Steps * 4);
            var normals = new List<Vector3>(Steps * 4);
            var triangles = new List<int>(Steps * 6);

            float laneEnd = Mathf.Min(throat, lane.Length);

            for (int i = 0; i < Steps; i++)
            {
                float t0 = i / (float)Steps;
                float t1 = (i + 1) / (float)Steps;

                Vector3 innerA = MainEdge(main, alongMain, mainShape, side, t0, throat);
                Vector3 innerB = MainEdge(main, alongMain, mainShape, side, t1, throat);
                Vector3 outerA = LaneEdge(lane, laneShape, laneEnd, t0);
                Vector3 outerB = LaneEdge(lane, laneShape, laneEnd, t1);

                AddQuad(vertices, normals, triangles, innerA, innerB, outerB, outerA);
            }

            if (triangles.Count == 0)
            {
                return null;
            }

            var mesh = new Mesh { name = meshName };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.subMeshCount = SubmeshCount;
            mesh.SetTriangles(triangles, SurfaceSubmesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A point on the main road's outer edge, swept from one side of the mouth to the other.
        ///
        /// Sampled from the path rather than interpolated between two corners, so the mouth follows the
        /// road where it curves instead of cutting the corner off.
        /// </summary>
        private static Vector3 MainEdge(
            IRoadPath main,
            float alongMain,
            in RoadShape shape,
            float side,
            float t,
            float throat)
        {
            float along = Mathf.Clamp(alongMain + Mathf.Lerp(-throat, throat, t), 0f, main.Length);

            Vector3 centre = main.GetPositionAtDistance(along);
            Vector3 right = main.GetRightAtDistance(along);

            return centre + right * (shape.OuterHalfWidth * side)
                   + Vector3.up * (shape.SurfaceLift - shape.ShoulderDrop);
        }

        /// <summary>A point across the lane's own cross-section, where the blend hands over to the ribbon.</summary>
        private static Vector3 LaneEdge(IRoadPath lane, in RoadShape shape, float atDistance, float t)
        {
            Vector3 centre = lane.GetPositionAtDistance(atDistance);
            Vector3 right = lane.GetRightAtDistance(atDistance);

            // t runs the opposite way across the lane from the way it runs along the main road, so the
            // strip does not cross over itself in the middle.
            float across = Mathf.Lerp(shape.OuterHalfWidth, -shape.OuterHalfWidth, t);

            return centre + right * across + Vector3.up * (shape.SurfaceLift - shape.ShoulderDrop);
        }

        private static void AddQuad(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d)
        {
            AddTriangle(vertices, normals, triangles, a, b, c);
            AddTriangle(vertices, normals, triangles, a, c, d);
        }

        private static void AddTriangle(
            List<Vector3> vertices,
            List<Vector3> normals,
            List<int> triangles,
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
                // A road surface is never seen from below, and the winding across the throat flips
                // depending on which side of the road the lane leaves.
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

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }
    }
}
