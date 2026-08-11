using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The village's working buildings: a windmill, barns and a sawmill.
    ///
    /// The windmill replaces what was a church, and it takes over the church's actual job — being the one
    /// thing in the village with a silhouette, visible from the pass road high above. That is why it is
    /// tall and why its sails are open latticework rather than solid panels: at a kilometre a solid disc
    /// reads as a blob, while a lattice reads as a mill even when it is four pixels across.
    ///
    /// Same conventions as <see cref="BuildingMeshes"/> — authored in the placement's local frame, drawn
    /// into a shared <see cref="VegetationMeshBuffer"/>, submesh constants from there so the whole village
    /// stays one mesh per tile.
    /// </summary>
    public static class MillMeshes
    {
        /// <summary>A tower mill: tapered octagonal body, gallery, cap and four lattice sails.</summary>
        public static void AddWindmill(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            int wall = BuildingMeshes.WallSubmesh(0);
            int roof = BuildingMeshes.RoofSubmesh(1);

            float height = random.Range(13f, 16f);
            float baseRadius = random.Range(3.4f, 4.0f);
            float topRadius = baseRadius * 0.62f;

            AddTaperedTower(buffer, place, wall, 8, baseRadius + 0.25f, baseRadius + 0.25f, -0.5f, 0.7f);
            AddTaperedTower(buffer, place, wall, 8, baseRadius, topRadius, 0f, height);

            // Gallery: a walkway ring about a third of the way up, which is what makes a tower mill
            // read as a tower mill rather than as a silo.
            float galleryY = height * 0.34f;
            float galleryRadius = Mathf.Lerp(baseRadius, topRadius, 0.34f);
            AddRing(buffer, place, BuildingMeshes.TrimSubmesh, 8, galleryRadius + 0.9f, galleryY, 0.14f);
            AddTaperedTower(buffer, place, BuildingMeshes.TrimSubmesh, 8,
                galleryRadius + 0.9f, galleryRadius + 0.9f, galleryY + 0.14f, galleryY + 0.95f);

            // Cap, and the windows under it.
            AddCone(buffer, place, roof, 8, topRadius + 0.3f, height, height + 2.6f);

            // Lit, all three of them. A working mill is one of three silhouettes the town is read by
            // from the pass, and a landmark that goes dark at night stops being one.
            for (int i = 0; i < 3; i++)
            {
                float angle = i * (Mathf.PI * 2f / 3f) + 0.4f;
                float r = Mathf.Lerp(baseRadius, topRadius, 0.55f);
                AddBox(buffer, place, BuildingMeshes.WindowLitSubmesh,
                    Mathf.Cos(angle) * r, height * 0.55f, Mathf.Sin(angle) * r, 0.4f, 0.85f, 0.4f);
            }

            AddDoor(buffer, place, baseRadius, 0f);
            AddSails(buffer, place, topRadius, height + 1.4f, ref random);
        }

        /// <summary>A long barn with a big cart door and boarded gable ends.</summary>
        public static void AddBarn(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            int wall = BuildingMeshes.WallSubmesh(2);
            int roof = BuildingMeshes.RoofSubmesh(2);

            float halfWidth = random.Range(5.5f, 7.5f);
            float halfDepth = random.Range(4.5f, 5.5f);
            float eave = random.Range(4.2f, 5.0f);
            float ridge = eave + random.Range(2.6f, 3.4f);

            AddBox(buffer, place, wall, 0f, -0.4f, 0f, halfWidth + 0.15f, 0.7f, halfDepth + 0.15f);
            AddBox(buffer, place, wall, 0f, 0f, 0f, halfWidth, eave, halfDepth);
            AddGable(buffer, place, wall, roof, halfWidth, halfDepth, eave, ridge, 0.5f);

            // Cart door, tall enough to take a loaded wagon.
            AddBox(buffer, place, BuildingMeshes.TrimSubmesh, 0f, 0f, halfDepth + 0.04f,
                halfWidth * 0.42f, eave * 0.78f, 0.06f);

            // Boarding: vertical battens down the street face, which is what stops a barn wall being one
            // enormous blank rectangle.
            int battens = Mathf.Max(3, Mathf.FloorToInt(halfWidth));
            for (int i = 0; i < battens; i++)
            {
                float x = Mathf.Lerp(-halfWidth * 0.9f, halfWidth * 0.9f, i / (float)(battens - 1));
                if (Mathf.Abs(x) < halfWidth * 0.5f)
                {
                    continue;
                }

                AddBox(buffer, place, BuildingMeshes.TrimSubmesh, x, 0f, halfDepth + 0.03f,
                    0.09f, eave, 0.05f);
            }
        }

        /// <summary>An open-sided sawmill shed on posts, with timber stacked beside it.</summary>
        public static void AddSawmill(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            int roof = BuildingMeshes.RoofSubmesh(0);

            float halfWidth = random.Range(4.5f, 6f);
            float halfDepth = random.Range(3.5f, 4.5f);
            float postHeight = random.Range(3.4f, 4.0f);

            // Four corner posts, open on every side — the roof is the whole building.
            for (int i = 0; i < 4; i++)
            {
                float sx = (i & 1) == 0 ? -1f : 1f;
                float sz = (i & 2) == 0 ? -1f : 1f;
                AddBox(buffer, place, BuildingMeshes.TrimSubmesh,
                    sx * halfWidth, -0.3f, sz * halfDepth, 0.16f, postHeight + 0.3f, 0.16f);
            }

            // A lean-to roof, sloping to the back.
            Vector3 highLeft = place.ToWorld(-halfWidth - 0.5f, postHeight + 0.9f, halfDepth + 0.5f);
            Vector3 highRight = place.ToWorld(halfWidth + 0.5f, postHeight + 0.9f, halfDepth + 0.5f);
            Vector3 lowLeft = place.ToWorld(-halfWidth - 0.5f, postHeight, -halfDepth - 0.5f);
            Vector3 lowRight = place.ToWorld(halfWidth + 0.5f, postHeight, -halfDepth - 0.5f);

            // Genuinely two-sided: an open-sided shed is seen from underneath as often as from above.
            buffer.AddQuadFacing(roof, highLeft, highRight, lowRight, lowLeft, place.Up);
            buffer.AddQuadFacing(roof, lowLeft, lowRight, highRight, highLeft, -place.Up);

            // Timber stacks. The reason to have a sawmill at all is that it comes with a reason for
            // stacked wood, and stacked wood is what makes a yard look worked in.
            int stacks = random.Chance(0.5f) ? 3 : 2;
            for (int i = 0; i < stacks; i++)
            {
                float x = Mathf.Lerp(-halfWidth * 0.7f, halfWidth * 0.7f, i / Mathf.Max(1f, stacks - 1f));
                float z = -halfDepth - random.Range(2.5f, 4.5f);
                float logHeight = random.Range(0.9f, 1.6f);

                AddBox(buffer, place, BuildingMeshes.TrimSubmesh, x, 0f, z,
                    random.Range(0.8f, 1.2f), logHeight, random.Range(1.6f, 2.4f));
            }
        }

        /// <summary>Four sails on a hub, each an open lattice of a spar and its bars.</summary>
        private static void AddSails(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float topRadius,
            float hubY,
            ref PlantRandom random)
        {
            float sailLength = random.Range(6.5f, 8f);
            float lean = random.Range(0.1f, 0.35f);
            float z = topRadius + 0.9f;

            AddBox(buffer, place, BuildingMeshes.TrimSubmesh, 0f, hubY - 0.35f, z - 0.3f, 0.35f, 0.7f, 0.5f);

            for (int sail = 0; sail < 4; sail++)
            {
                float angle = sail * (Mathf.PI * 0.5f) + lean;
                float dirX = Mathf.Cos(angle);
                float dirY = Mathf.Sin(angle);

                // The spar.
                for (int seg = 0; seg < 5; seg++)
                {
                    float t = (seg + 0.5f) / 5f;
                    float r = sailLength * t;
                    AddBox(buffer, place, BuildingMeshes.TrimSubmesh,
                        dirX * r, hubY + dirY * r, z, 0.5f, 0.14f, 0.1f);
                }

                // Cross bars, giving the lattice its openness.
                for (int bar = 1; bar <= 3; bar++)
                {
                    float r = sailLength * (bar / 4f);
                    AddBox(buffer, place, BuildingMeshes.TrimSubmesh,
                        dirX * r, hubY + dirY * r, z, 0.09f, 1.5f, 0.09f);
                }
            }
        }

        /// <summary>An n-sided tapered tower with a lid, in the placement's local frame.</summary>
        private static void AddTaperedTower(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float bottomRadius,
            float topRadius,
            float bottomY,
            float topY)
        {
            float step = Mathf.PI * 2f / sides;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 b0 = place.ToWorld(Mathf.Cos(a0) * bottomRadius, bottomY, Mathf.Sin(a0) * bottomRadius);
                Vector3 b1 = place.ToWorld(Mathf.Cos(a1) * bottomRadius, bottomY, Mathf.Sin(a1) * bottomRadius);
                Vector3 t0 = place.ToWorld(Mathf.Cos(a0) * topRadius, topY, Mathf.Sin(a0) * topRadius);
                Vector3 t1 = place.ToWorld(Mathf.Cos(a1) * topRadius, topY, Mathf.Sin(a1) * topRadius);

                // Exact outward for a frustum: radial, tilted up by however much the wall leans in.
                Vector3 outward = Radial(place, (a0 + a1) * 0.5f) * (topY - bottomY)
                                  + place.Up * (bottomRadius - topRadius);

                buffer.AddQuadFacing(submesh, b0, t0, t1, b1, outward);
            }
        }

        /// <summary>A flat annular ring — the gallery deck.</summary>
        private static void AddRing(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float atY,
            float thickness)
        {
            AddTaperedTower(buffer, place, submesh, sides, radius, radius, atY, atY + thickness);

            float step = Mathf.PI * 2f / sides;
            Vector3 centre = place.ToWorld(0f, atY + thickness, 0f);

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 p0 = place.ToWorld(Mathf.Cos(a0) * radius, atY + thickness, Mathf.Sin(a0) * radius);
                Vector3 p1 = place.ToWorld(Mathf.Cos(a1) * radius, atY + thickness, Mathf.Sin(a1) * radius);

                buffer.AddTriangleFacing(submesh, p1, p0, centre, place.Up);
            }
        }

        private static void AddCone(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float baseY,
            float apexY)
        {
            float step = Mathf.PI * 2f / sides;
            Vector3 apex = place.ToWorld(0f, apexY, 0f);

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 p0 = place.ToWorld(Mathf.Cos(a0) * radius, baseY, Mathf.Sin(a0) * radius);
                Vector3 p1 = place.ToWorld(Mathf.Cos(a1) * radius, baseY, Mathf.Sin(a1) * radius);

                Vector3 outward = Radial(place, (a0 + a1) * 0.5f) * (apexY - baseY) + place.Up * radius;

                buffer.AddTriangleFacing(submesh, p0, apex, p1, outward);
            }
        }

        private static void AddDoor(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float radius,
            float atY)
        {
            AddBox(buffer, place, BuildingMeshes.TrimSubmesh, 0f, atY, radius * 0.95f, 0.6f, 2.1f, 0.12f);
        }

        /// <summary>A pitched roof over a rectangular body, with its gable triangles.</summary>
        private static void AddGable(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wallSubmesh,
            int roofSubmesh,
            float halfWidth,
            float halfDepth,
            float eaveHeight,
            float ridgeHeight,
            float overhang)
        {
            float ex = halfWidth + overhang;
            float ez = halfDepth + overhang;

            Vector3 frontLeft = place.ToWorld(-ex, eaveHeight, ez);
            Vector3 frontRight = place.ToWorld(ex, eaveHeight, ez);
            Vector3 backLeft = place.ToWorld(-ex, eaveHeight, -ez);
            Vector3 backRight = place.ToWorld(ex, eaveHeight, -ez);
            Vector3 ridgeLeft = place.ToWorld(-ex, ridgeHeight, 0f);
            Vector3 ridgeRight = place.ToWorld(ex, ridgeHeight, 0f);

            // Both slopes, then both undersides — the negated hint is what marks the last two as deliberate.
            buffer.AddQuadFacing(roofSubmesh, frontLeft, frontRight, ridgeRight, ridgeLeft, place.Up);
            buffer.AddQuadFacing(roofSubmesh, ridgeLeft, ridgeRight, backRight, backLeft, place.Up);
            buffer.AddQuadFacing(roofSubmesh, ridgeLeft, ridgeRight, frontRight, frontLeft, -place.Up);
            buffer.AddQuadFacing(roofSubmesh, backLeft, backRight, ridgeRight, ridgeLeft, -place.Up);

            Vector3 gableRightFront = place.ToWorld(halfWidth, eaveHeight, halfDepth);
            Vector3 gableRightBack = place.ToWorld(halfWidth, eaveHeight, -halfDepth);
            Vector3 gableRightTop = place.ToWorld(halfWidth, ridgeHeight, 0f);
            buffer.AddTriangleFacing(wallSubmesh, gableRightBack, gableRightTop, gableRightFront, place.Right);

            Vector3 gableLeftFront = place.ToWorld(-halfWidth, eaveHeight, halfDepth);
            Vector3 gableLeftBack = place.ToWorld(-halfWidth, eaveHeight, -halfDepth);
            Vector3 gableLeftTop = place.ToWorld(-halfWidth, ridgeHeight, 0f);
            buffer.AddTriangleFacing(wallSubmesh, gableLeftFront, gableLeftTop, gableLeftBack, -place.Right);
        }

        private static void AddBox(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            float centerX,
            float baseY,
            float centerZ,
            float halfWidth,
            float height,
            float halfDepth)
        {
            float x0 = centerX - halfWidth;
            float x1 = centerX + halfWidth;
            float z0 = centerZ - halfDepth;
            float z1 = centerZ + halfDepth;
            float y0 = baseY;
            float y1 = baseY + height;

            Vector3 a = place.ToWorld(x0, y0, z0);
            Vector3 b = place.ToWorld(x1, y0, z0);
            Vector3 c = place.ToWorld(x1, y0, z1);
            Vector3 d = place.ToWorld(x0, y0, z1);
            Vector3 e = place.ToWorld(x0, y1, z0);
            Vector3 f = place.ToWorld(x1, y1, z0);
            Vector3 g = place.ToWorld(x1, y1, z1);
            Vector3 h = place.ToWorld(x0, y1, z1);

            buffer.AddQuadFacing(submesh, d, c, g, h, place.Forward);
            buffer.AddQuadFacing(submesh, b, a, e, f, -place.Forward);
            buffer.AddQuadFacing(submesh, c, b, f, g, place.Right);
            buffer.AddQuadFacing(submesh, a, d, h, e, -place.Right);
            buffer.AddQuadFacing(submesh, h, g, f, e, place.Up);
            buffer.AddQuadFacing(submesh, b, c, d, a, -place.Up);
        }

        /// <summary>The world direction of the local radial at one angle — the outward hint for anything round.</summary>
        private static Vector3 Radial(in PlantPlacement place, float angle)
        {
            return place.Right * Mathf.Cos(angle) + place.Forward * Mathf.Sin(angle);
        }
    }
}
