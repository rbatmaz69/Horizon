using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The buildings a town is recognised by from a distance.
    ///
    /// <para>Its own file rather than an addition to <see cref="MillMeshes"/>, which is the working
    /// buildings and has a clear remit, or to <see cref="BuildingMeshes"/>, which is the ordinary stock.
    /// A landmark is a different kind of thing: it is built to be seen from two kilometres away, it costs
    /// several times what a house costs, and there is one of it rather than three hundred.</para>
    ///
    /// <para>Submesh constants come from <see cref="BuildingMeshes"/> so a landmark lands in the same
    /// mesh as the houses around it and adds no draw call of its own.</para>
    /// </summary>
    public static class LandmarkMeshes
    {
        /// <summary>Height of the minaret's finial above the ground, metres.</summary>
        public const float MinaretHeight = 33f;

        /// <summary>
        /// A mosque: prayer hall, dome and minaret. About 450 triangles.
        ///
        /// <para>Two shapes carry the whole thing at the distance it is seen from, and the rest is there
        /// to keep them company. The <b>dome</b> is the only curved mass in a town built entirely out of
        /// boxes and pitched roofs, so it reads as different long before it reads as a dome. And the
        /// <b>minaret</b> at 33 m against the windmill's 16 is what makes the town a town rather than a
        /// village with more houses in it — a slender vertical stays visible at distances where a wide
        /// building of the same height has already merged into the roofline.</para>
        ///
        /// <para>The balcony a third of the way up is not decoration either: an evenly tapering shaft
        /// reads as a chimney, and one horizontal line across it is the whole difference.</para>
        /// </summary>
        public static void AddMosque(VegetationMeshBuffer buffer, in PlantPlacement place, ref PlantRandom random)
        {
            const int wall = BuildingMeshes.FirstWallSubmesh;
            const int roof = BuildingMeshes.FirstRoofSubmesh + 1;
            const int trim = BuildingMeshes.TrimSubmesh;

            const float hallHalf = 9f;
            const float hallHeight = 8.5f;

            // The prayer hall: square in plan, which is unlike anything else in the town and is most of
            // why it does not read as a large house.
            BuildingMeshes.AddBox(buffer, place, wall, 0f, 0f, 0f, hallHalf, hallHeight, hallHalf);

            // A parapet standing proud of the wall, so the flat roof has an edge rather than stopping.
            BuildingMeshes.AddBox(buffer, place, trim, 0f, hallHeight, 0f,
                hallHalf + 0.3f, 0.7f, hallHalf + 0.3f);

            AddPorch(buffer, place, wall, trim, hallHalf, 3.6f);

            // Generous, at 0.7: the hall is the one interior in the town that is meant to look occupied
            // after dark, and a band of openings with a third of them lit reads as a derelict.
            AddWindowBand(buffer, place, hallHalf, 3.2f, 0.7f, ref random);

            // The dome, on a low drum. The drum is what stops it looking like a bowl set on a table: a
            // hemisphere meeting a flat roof directly has no shadow line at its foot.
            const float drumTop = hallHeight + 1.6f;
            AddPrism(buffer, place, wall, 12, 6.4f, 6.4f, hallHeight + 0.7f, drumTop, 0f, 0f);
            AddDome(buffer, place, roof, 12, 6.4f, drumTop, drumTop + 5.6f);
            AddFinial(buffer, place, trim, 0f, drumTop + 5.6f, 0f, 1.4f);

            // The minaret, off one back corner of the hall so it is clear of the porch and reads as its
            // own mass rather than as a turret on the building.
            AddMinaret(buffer, place, wall, trim, hallHalf - 1.6f, -(hallHalf - 1.6f));
        }

        /// <summary>The shaft, balcony, cap and finial of a minaret, as one call.</summary>
        private static void AddMinaret(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wall,
            int trim,
            float centreX,
            float centreZ)
        {
            const int sides = 8;
            const float balconyAt = 18f;
            const float capFrom = 26.5f;

            AddPrism(buffer, place, wall, sides, 1.35f, 1.15f, 0f, balconyAt, centreX, centreZ);

            // The balcony: a disc standing well proud of the shaft, with a parapet on it.
            AddPrism(buffer, place, trim, sides, 2.05f, 2.05f, balconyAt, balconyAt + 0.35f, centreX, centreZ);
            AddPrism(buffer, place, trim, sides, 1.95f, 1.95f, balconyAt + 0.35f, balconyAt + 1.2f,
                centreX, centreZ);

            // Openings under the balcony, always lit — never rolled. A minaret that lights is what makes
            // the town read at night from the pass at all, and it is the one thing in the place whose
            // whole job is to be a light in the distance.
            AddMinaretLights(buffer, place, sides, 1.36f, balconyAt - 2.6f, 1.8f, centreX, centreZ);

            AddPrism(buffer, place, wall, sides, 1.1f, 0.95f, balconyAt + 1.2f, capFrom, centreX, centreZ);
            AddCone(buffer, place, BuildingMeshes.FirstRoofSubmesh + 1, sides, 1.15f, capFrom, 31.4f,
                centreX, centreZ);
            AddFinial(buffer, place, trim, centreX, 31.4f, centreZ, 1.6f);
        }

        /// <summary>A porch across the street face: a flat slab on four square piers.</summary>
        private static void AddPorch(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wall,
            int trim,
            float hallHalf,
            float depth)
        {
            const float height = 5.2f;
            float front = hallHalf + depth;

            for (int i = 0; i < 4; i++)
            {
                float x = Mathf.Lerp(-hallHalf + 1.2f, hallHalf - 1.2f, i / 3f);
                BuildingMeshes.AddBox(buffer, place, wall, x, 0f, front - 0.7f, 0.5f, height, 0.5f);
            }

            BuildingMeshes.AddBox(buffer, place, trim, 0f, height, (hallHalf + front) * 0.5f,
                hallHalf, 0.6f, depth * 0.5f + 0.2f);
        }

        /// <summary>A band of tall openings all round the hall, in the window submeshes.</summary>
        private static void AddWindowBand(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float hallHalf,
            float sillY,
            float litChance,
            ref PlantRandom random)
        {
            const float half = 0.7f;
            const float height = 2.8f;
            float out0 = hallHalf + 0.02f;

            for (int i = 0; i < 4; i++)
            {
                float x = Mathf.Lerp(-hallHalf * 0.62f, hallHalf * 0.62f, i / 3f);

                // Rolled per opening rather than per bay, so the four walls do not light in lockstep.
                Add(BuildingMeshes.GlassSubmesh(ref random, litChance), place.Forward,
                    place.ToWorld(x - half, sillY, out0), place.ToWorld(x + half, sillY, out0),
                    place.ToWorld(x + half, sillY + height, out0), place.ToWorld(x - half, sillY + height, out0));

                Add(BuildingMeshes.GlassSubmesh(ref random, litChance), -place.Forward,
                    place.ToWorld(x + half, sillY, -out0), place.ToWorld(x - half, sillY, -out0),
                    place.ToWorld(x - half, sillY + height, -out0), place.ToWorld(x + half, sillY + height, -out0));

                Add(BuildingMeshes.GlassSubmesh(ref random, litChance), place.Right,
                    place.ToWorld(out0, sillY, x + half), place.ToWorld(out0, sillY, x - half),
                    place.ToWorld(out0, sillY + height, x - half), place.ToWorld(out0, sillY + height, x + half));

                Add(BuildingMeshes.GlassSubmesh(ref random, litChance), -place.Right,
                    place.ToWorld(-out0, sillY, x - half), place.ToWorld(-out0, sillY, x + half),
                    place.ToWorld(-out0, sillY + height, x + half), place.ToWorld(-out0, sillY + height, x - half));
            }

            void Add(int submesh, Vector3 outward, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                buffer.AddQuadFacing(submesh, a, b, c, d, outward);
            }
        }

        /// <summary>Small lit openings round a minaret shaft, on every other facet.</summary>
        private static void AddMinaretLights(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int sides,
            float radius,
            float sillY,
            float height,
            float centreX,
            float centreZ)
        {
            float step = Mathf.PI * 2f / sides;

            for (int i = 0; i < sides; i += 2)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 outward = place.Right * Mathf.Cos((a0 + a1) * 0.5f)
                                  + place.Forward * Mathf.Sin((a0 + a1) * 0.5f);

                buffer.AddQuadFacing(
                    BuildingMeshes.WindowLitSubmesh,
                    Ring(place, a0, radius, sillY, centreX, centreZ),
                    Ring(place, a0, radius, sillY + height, centreX, centreZ),
                    Ring(place, a1, radius, sillY + height, centreX, centreZ),
                    Ring(place, a1, radius, sillY, centreX, centreZ),
                    outward);
            }
        }

        /// <summary>
        /// A dome, as stacked rings following a quarter-circle profile.
        ///
        /// Four rings rather than one cone, because the whole reason the dome is here is that it is the
        /// one curved mass in a town of boxes — and a cone would simply be another pitched roof. Four is
        /// where the silhouette stops being obviously faceted at the distance this is seen from, and the
        /// rings crowd towards the top because that is where the curvature is.
        /// </summary>
        public static void AddDome(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float baseY,
            float apexY)
        {
            const int rings = 4;
            float step = Mathf.PI * 2f / sides;
            float rise = apexY - baseY;

            for (int ring = 0; ring < rings; ring++)
            {
                float t0 = ring / (float)rings;
                float t1 = (ring + 1) / (float)rings;

                float lowRadius = radius * Mathf.Cos(t0 * Mathf.PI * 0.5f);
                float highRadius = radius * Mathf.Cos(t1 * Mathf.PI * 0.5f);
                float lowY = baseY + rise * Mathf.Sin(t0 * Mathf.PI * 0.5f);
                float highY = baseY + rise * Mathf.Sin(t1 * Mathf.PI * 0.5f);

                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * step;
                    float a1 = (i + 1) * step;

                    Vector3 outward = place.Right * Mathf.Cos((a0 + a1) * 0.5f)
                                      + place.Forward * Mathf.Sin((a0 + a1) * 0.5f)
                                      + place.Up * t0;

                    Vector3 b0 = Ring(place, a0, lowRadius, lowY, 0f, 0f);
                    Vector3 b1 = Ring(place, a1, lowRadius, lowY, 0f, 0f);

                    if (ring == rings - 1)
                    {
                        buffer.AddTriangleFacing(submesh, b0, place.ToWorld(0f, apexY, 0f), b1, outward);
                        continue;
                    }

                    buffer.AddQuadFacing(
                        submesh,
                        b0,
                        Ring(place, a0, highRadius, highY, 0f, 0f),
                        Ring(place, a1, highRadius, highY, 0f, 0f),
                        b1,
                        outward);
                }
            }
        }

        /// <summary>An n-gon prism, open at both ends — they are always covered by what stands on them.</summary>
        private static void AddPrism(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float bottomRadius,
            float topRadius,
            float bottomY,
            float topY,
            float centreX,
            float centreZ)
        {
            float step = Mathf.PI * 2f / sides;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 outward = place.Right * Mathf.Cos((a0 + a1) * 0.5f)
                                  + place.Forward * Mathf.Sin((a0 + a1) * 0.5f);

                buffer.AddQuadFacing(
                    submesh,
                    Ring(place, a0, bottomRadius, bottomY, centreX, centreZ),
                    Ring(place, a0, topRadius, topY, centreX, centreZ),
                    Ring(place, a1, topRadius, topY, centreX, centreZ),
                    Ring(place, a1, bottomRadius, bottomY, centreX, centreZ),
                    outward);
            }
        }

        /// <summary>An n-gon cone, for the minaret's cap.</summary>
        private static void AddCone(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float baseY,
            float apexY,
            float centreX,
            float centreZ)
        {
            float step = Mathf.PI * 2f / sides;
            Vector3 apex = place.ToWorld(centreX, apexY, centreZ);

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                Vector3 outward = place.Right * Mathf.Cos((a0 + a1) * 0.5f)
                                  + place.Forward * Mathf.Sin((a0 + a1) * 0.5f);

                buffer.AddTriangleFacing(
                    submesh,
                    Ring(place, a0, radius, baseY, centreX, centreZ),
                    apex,
                    Ring(place, a1, radius, baseY, centreX, centreZ),
                    outward);
            }
        }

        /// <summary>A slender finial: a short stem with a crescent above it.</summary>
        private static void AddFinial(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            float centreX,
            float baseY,
            float centreZ,
            float height)
        {
            BuildingMeshes.AddBox(buffer, place, submesh, centreX, baseY, centreZ, 0.09f, height, 0.09f);

            // The crescent as three thin boxes in a shallow arc. At the size this is ever seen it is a
            // notched ring, and a notched ring is unmistakable in silhouette; a real curve would cost
            // thirty triangles to say the same thing.
            float top = baseY + height;
            for (int i = 0; i < 3; i++)
            {
                float angle = Mathf.Lerp(-0.9f, 0.9f, i / 2f);
                float x = centreX + Mathf.Sin(angle) * 0.55f;
                float y = top + 0.55f - Mathf.Cos(angle) * 0.2f;

                BuildingMeshes.AddBox(buffer, place, submesh, x, y, centreZ, 0.12f, 0.34f, 0.08f);
            }
        }

        private static Vector3 Ring(
            in PlantPlacement place, float angle, float radius, float y, float centreX, float centreZ)
        {
            return place.ToWorld(
                centreX + Mathf.Cos(angle) * radius, y, centreZ + Mathf.Sin(angle) * radius);
        }
    }
}
