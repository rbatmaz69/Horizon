using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The buildings a town is recognised by from a distance.
    ///
    /// <para>Its own file rather than an addition to <see cref="MillMeshes"/>, which is the working
    /// buildings and has a clear remit, or to <see cref="BuildingMeshes"/>, which is the ordinary stock.
    /// A landmark is a different kind of thing: it is built to be seen from two kilometres away, it costs
    /// several times what a house costs, and there are three of them rather than three hundred.</para>
    ///
    /// <para>Submesh constants come from <see cref="BuildingMeshes"/> so a landmark lands in the same
    /// mesh as the houses around it and adds no draw call of its own.</para>
    /// </summary>
    public static class LandmarkMeshes
    {
        /// <summary>Total height of the church, ground to the tip of the cross, metres.</summary>
        public const float ChurchHeight = 34f;

        /// <summary>
        /// A church: nave, lower chancel, west tower, belfry and a two-stage spire. About 400 triangles.
        ///
        /// <para>Two things here are worth the triangles they cost and nothing else is. The <b>tower</b>
        /// at 34 m against the windmill's 16: a town's landmark has to out-scale a village's, or the
        /// place still reads as a village with more houses in it. And the <b>two-stage taper</b> on the
        /// spire — a plain cone silhouettes as a party hat, while a spire that narrows sharply and then
        /// runs on thin reads as a spire at four pixels, which is the size it will usually be seen at.</para>
        /// </summary>
        public static void AddChurch(VegetationMeshBuffer buffer, in PlantPlacement place, ref PlantRandom random)
        {
            const int wall = BuildingMeshes.FirstWallSubmesh;
            const int roof = BuildingMeshes.FirstRoofSubmesh + 1;

            const float naveHalfWidth = 6.5f;
            const float naveHalfDepth = 11f;
            const float naveEave = 9f;
            const float naveRidge = 14.5f;

            // Nave. Its ridge runs across the placement's Z, so the long axis faces the street the plot
            // was laid against.
            BuildingMeshes.AddBox(buffer, place, wall, 0f, 0f, 0f, naveHalfWidth, naveEave, naveHalfDepth);
            BuildingMeshes.AddGableRoof(buffer, place, wall, roof,
                naveHalfWidth, naveHalfDepth, naveEave, naveRidge, 0.6f);

            // Chancel: lower, shorter, off the far end. The step down in ridge height is most of what
            // stops the body of the church reading as a shed.
            const float chancelHalfWidth = 4.6f;
            const float chancelHalfDepth = 4.5f;
            float chancelZ = -(naveHalfDepth + chancelHalfDepth - 0.5f);

            BuildingMeshes.AddBox(buffer, place, wall, 0f, 0f, chancelZ,
                chancelHalfWidth, 6.6f, chancelHalfDepth);
            AddChancelRoof(buffer, place, roof, chancelHalfWidth, chancelHalfDepth, chancelZ, 6.6f, 10f);

            // Tower at the near end, standing proud of the nave so it has four faces of its own.
            const float towerHalf = 3.6f;
            const float towerHeight = 22f;
            float towerZ = naveHalfDepth + towerHalf - 1.2f;

            BuildingMeshes.AddBox(buffer, place, wall, 0f, 0f, towerZ, towerHalf, towerHeight, towerHalf);

            // A corbel band: one course standing 0.35 m proud, just under the belfry. Two dozen triangles
            // that stop 22 m of blank wall reading as a chimney.
            BuildingMeshes.AddBox(buffer, place, BuildingMeshes.TrimSubmesh, 0f, towerHeight - 3.4f, towerZ,
                towerHalf + 0.35f, 0.5f, towerHalf + 0.35f);

            AddBelfry(buffer, place, towerHalf, towerZ, towerHeight - 2.2f);

            // The spire, and the cross on top of it.
            AddSpire(buffer, place, roof, 8, towerHalf * 0.95f, towerHeight, ChurchHeight - 1.6f, towerZ);

            AddCross(buffer, place, towerZ, ChurchHeight - 1.6f);

            // A door, so the front reads as a way in rather than as a wall.
            BuildingMeshes.AddBox(buffer, place, BuildingMeshes.TrimSubmesh,
                0f, 0f, towerZ + towerHalf - 0.05f, 1.1f, 3.2f, 0.25f);
        }

        /// <summary>
        /// A two-stage tapered spire on an n-gon plan, centred on <paramref name="centreZ"/>.
        ///
        /// The break is at a third of the height, where the taper goes from fast to slow. That is the
        /// whole trick: the silhouette gets a shoulder, and a shoulder is what the eye reads as a spire
        /// rather than as a cone.
        /// </summary>
        public static void AddSpire(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float baseY,
            float apexY,
            float centreZ)
        {
            float step = Mathf.PI * 2f / sides;
            float span = apexY - baseY;
            float breakY = baseY + span * 0.32f;
            float breakRadius = radius * 0.42f;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 outward = place.Right * Mathf.Cos((a0 + a1) * 0.5f)
                                  + place.Forward * Mathf.Sin((a0 + a1) * 0.5f);

                Vector3 b0 = Ring(place, a0, radius, baseY, centreZ);
                Vector3 b1 = Ring(place, a1, radius, baseY, centreZ);
                Vector3 m0 = Ring(place, a0, breakRadius, breakY, centreZ);
                Vector3 m1 = Ring(place, a1, breakRadius, breakY, centreZ);

                // Up the face first, then round the ring: the other order winds every panel of the
                // spire inwards, which the flip counter reported as twenty-four faces on a building
                // that has four hundred.
                buffer.AddQuadFacing(submesh, b0, m0, m1, b1, outward);
                buffer.AddTriangleFacing(submesh, m0, place.ToWorld(0f, apexY, centreZ), m1, outward);
            }
        }

        private static Vector3 Ring(in PlantPlacement place, float angle, float radius, float y, float centreZ)
        {
            return place.ToWorld(Mathf.Cos(angle) * radius, y, centreZ + Mathf.Sin(angle) * radius);
        }

        /// <summary>
        /// Four belfry openings, one to each face, in the window submesh.
        ///
        /// They are the reason the church works at night as well as by day: a lit belfry at 18 m is the
        /// one thing in the town visible from the pass road above once the sun has gone.
        /// </summary>
        private static void AddBelfry(
            VegetationMeshBuffer buffer, in PlantPlacement place, float towerHalf, float towerZ, float sillY)
        {
            const float half = 0.9f;
            const float height = 2.6f;
            float out0 = towerHalf + 0.02f;

            for (int face = 0; face < 4; face++)
            {
                float x0 = face == 0 ? -half : face == 1 ? half : -half;
                float x1 = face == 0 ? half : face == 1 ? -half : half;

                Vector3 a, b, c, d;
                Vector3 outward;

                switch (face)
                {
                    case 0:
                        outward = place.Forward;
                        a = place.ToWorld(x0, sillY, towerZ + out0);
                        b = place.ToWorld(x1, sillY, towerZ + out0);
                        c = place.ToWorld(x1, sillY + height, towerZ + out0);
                        d = place.ToWorld(x0, sillY + height, towerZ + out0);
                        break;

                    case 1:
                        outward = -place.Forward;
                        a = place.ToWorld(x0, sillY, towerZ - out0);
                        b = place.ToWorld(x1, sillY, towerZ - out0);
                        c = place.ToWorld(x1, sillY + height, towerZ - out0);
                        d = place.ToWorld(x0, sillY + height, towerZ - out0);
                        break;

                    case 2:
                        outward = place.Right;
                        a = place.ToWorld(out0, sillY, towerZ + x0);
                        b = place.ToWorld(out0, sillY, towerZ + x1);
                        c = place.ToWorld(out0, sillY + height, towerZ + x1);
                        d = place.ToWorld(out0, sillY + height, towerZ + x0);
                        break;

                    default:
                        outward = -place.Right;
                        a = place.ToWorld(-out0, sillY, towerZ + x0);
                        b = place.ToWorld(-out0, sillY, towerZ + x1);
                        c = place.ToWorld(-out0, sillY + height, towerZ + x1);
                        d = place.ToWorld(-out0, sillY + height, towerZ + x0);
                        break;
                }

                buffer.AddQuadFacing(BuildingMeshes.WindowSubmesh, a, b, c, d, outward);
            }
        }

        /// <summary>Two thin crossed boards on the apex. Twelve triangles, and unmistakable in silhouette.</summary>
        private static void AddCross(
            VegetationMeshBuffer buffer, in PlantPlacement place, float centreZ, float apexY)
        {
            const int trim = BuildingMeshes.TrimSubmesh;

            BuildingMeshes.AddBox(buffer, place, trim, 0f, apexY, centreZ, 0.09f, 2.2f, 0.09f);
            BuildingMeshes.AddBox(buffer, place, trim, 0f, apexY + 1.25f, centreZ, 0.75f, 0.16f, 0.09f);
        }

        /// <summary>A gable roof over the chancel, offset down the placement's Z axis.</summary>
        private static void AddChancelRoof(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int roofSubmesh,
            float halfWidth,
            float halfDepth,
            float centreZ,
            float eaveHeight,
            float ridgeHeight)
        {
            float ex = halfWidth + 0.4f;
            float z0 = centreZ - halfDepth - 0.4f;
            float z1 = centreZ + halfDepth + 0.4f;

            Vector3 eaveFrontLeft = place.ToWorld(-ex, eaveHeight, z1);
            Vector3 eaveFrontRight = place.ToWorld(ex, eaveHeight, z1);
            Vector3 eaveBackLeft = place.ToWorld(-ex, eaveHeight, z0);
            Vector3 eaveBackRight = place.ToWorld(ex, eaveHeight, z0);
            Vector3 ridgeLeft = place.ToWorld(-ex, ridgeHeight, centreZ);
            Vector3 ridgeRight = place.ToWorld(ex, ridgeHeight, centreZ);

            buffer.AddQuadFacing(roofSubmesh, eaveFrontLeft, eaveFrontRight, ridgeRight, ridgeLeft, place.Up);
            buffer.AddQuadFacing(roofSubmesh, ridgeLeft, ridgeRight, eaveBackRight, eaveBackLeft, place.Up);

            Vector3 gableRight = place.ToWorld(halfWidth, ridgeHeight, centreZ);
            buffer.AddTriangleFacing(BuildingMeshes.FirstWallSubmesh,
                place.ToWorld(halfWidth, eaveHeight, z0 + 0.4f), gableRight,
                place.ToWorld(halfWidth, eaveHeight, z1 - 0.4f), place.Right);

            Vector3 gableLeft = place.ToWorld(-halfWidth, ridgeHeight, centreZ);
            buffer.AddTriangleFacing(BuildingMeshes.FirstWallSubmesh,
                place.ToWorld(-halfWidth, eaveHeight, z1 - 0.4f), gableLeft,
                place.ToWorld(-halfWidth, eaveHeight, z0 + 0.4f), -place.Right);
        }
    }
}
