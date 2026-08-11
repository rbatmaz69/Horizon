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

            // A two-stage taper rather than the plain cone this used to be. A cone silhouettes as a party
            // hat; a shaft that leaves the shoulder almost vertical and then turns is what reads as a
            // spire, and at the distance the minaret is actually judged from — four or five pixels seen
            // from the pass — the silhouette is the entire building.
            AddSpire(buffer, place, BuildingMeshes.FirstRoofSubmesh + 1, sides, 1.15f, capFrom, 31.4f,
                0.62f, centreX, centreZ);
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

        /// <summary>
        /// A spire: a two-stage taper about a vertical axis, drawn as a shoulder stage and a point stage.
        ///
        /// <para><paramref name="entasis"/> is where the break sits, as a fraction of the height. Below
        /// it the shaft loses only a third of its radius, so the sides leave the shoulder close to
        /// vertical; above it the remainder runs to a point. That is the whole trick, and the reason it
        /// matters is scale: a spire is judged from a kilometre away, where it is a few pixels wide and
        /// nothing survives but the outline. A single taper from base to apex is a cone, a cone is a
        /// triangle, and a triangle on a tower reads as a party hat rather than as a spire.</para>
        ///
        /// <para>Sixteen triangles for eight sides — a quarter of what a curved profile would cost to say
        /// the same thing, which at four pixels it would not say any better.</para>
        /// </summary>
        public static void AddSpire(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float baseY,
            float apexY,
            float entasis,
            float centreX = 0f,
            float centreZ = 0f)
        {
            float breakY = Mathf.Lerp(baseY, apexY, Mathf.Clamp(entasis, 0.15f, 0.85f));

            AddPrism(buffer, place, submesh, sides, radius, radius * 0.68f, baseY, breakY,
                centreX, centreZ);
            AddCone(buffer, place, submesh, sides, radius * 0.68f, breakY, apexY, centreX, centreZ);
        }

        /// <summary>
        /// The town hall, on the uphill edge of the market square: a three-storey block with a
        /// ground-floor arcade, a hipped roof, a clock gable and a bell turret. About 320 triangles.
        ///
        /// <para>Three things separate it from a large house, and they are all silhouette. The
        /// <b>arcade</b> — an open colonnade at ground level — is the only building in the town you can
        /// see daylight through, and that alone says civic before any detail resolves. The <b>hipped</b>
        /// roof slopes on all four sides where every other roof here is gabled, so it reads as a
        /// different kind of building from directly across the square. And the <b>clock gable</b> breaks
        /// the eaves line at the middle of the front, which is where the eye lands anyway.</para>
        /// </summary>
        public static void AddTownHall(
            VegetationMeshBuffer buffer, in PlantPlacement place, ref PlantRandom random)
        {
            const int wall = BuildingMeshes.FirstWallSubmesh + 1;
            const int roof = BuildingMeshes.FirstRoofSubmesh + 1;
            const int trim = BuildingMeshes.TrimSubmesh;

            float halfWidth = random.Range(10.5f, 12f);
            float halfDepth = random.Range(7f, 8f);
            const float arcadeHeight = 4.2f;
            float eaveHeight = arcadeHeight + random.Range(7.4f, 8.4f);

            // The plinth and the two upper storeys are one mass; the ground floor is the arcade below it,
            // so the block is carried on piers rather than standing on the paving.
            BuildingMeshes.AddBox(buffer, place, wall, 0f, -0.5f, 0f,
                halfWidth + 0.25f, 0.8f, halfDepth + 0.25f);
            BuildingMeshes.AddBox(buffer, place, wall, 0f, arcadeHeight, 0f,
                halfWidth, eaveHeight - arcadeHeight, halfDepth);

            AddArcade(buffer, place, wall, trim, halfWidth, halfDepth, arcadeHeight);

            // Two bands: the arcade's springing line and the floor above it. A civic front is horizontal
            // where a house front is vertical, and the bands are what say so.
            BuildingMeshes.AddBox(buffer, place, trim, 0f, arcadeHeight - 0.18f, 0f,
                halfWidth + 0.2f, 0.36f, halfDepth + 0.2f);
            BuildingMeshes.AddBox(buffer, place, trim, 0f, arcadeHeight + 4.0f, 0f,
                halfWidth + 0.12f, 0.22f, halfDepth + 0.12f);

            AddCivicWindows(buffer, place, halfWidth, halfDepth, arcadeHeight + 1.1f, 5, ref random);
            AddCivicWindows(buffer, place, halfWidth, halfDepth, arcadeHeight + 5.1f, 5, ref random);

            AddHippedRoof(buffer, place, roof, halfWidth, halfDepth, eaveHeight,
                eaveHeight + random.Range(3.2f, 3.8f), 0.55f);

            AddClockGable(buffer, place, wall, roof, trim, halfDepth, eaveHeight);

            // The turret sits behind the ridge rather than on the front, so it does not fight the clock
            // gable for the same silhouette.
            const float turretFoot = 1.5f;
            float turretBase = eaveHeight + 2.6f;
            BuildingMeshes.AddBox(buffer, place, wall, 0f, turretBase, -1.4f,
                turretFoot, 2.2f, turretFoot);
            AddPrism(buffer, place, trim, 8, turretFoot + 0.25f, turretFoot + 0.25f,
                turretBase + 2.2f, turretBase + 2.5f, 0f, -1.4f);
            AddSpire(buffer, place, roof, 8, turretFoot + 0.1f,
                turretBase + 2.5f, turretBase + 8.2f, 0.55f, 0f, -1.4f);
        }

        /// <summary>An open colonnade across the front and both flanks: piers with the mass sitting on them.</summary>
        private static void AddArcade(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wall,
            int trim,
            float halfWidth,
            float halfDepth,
            float height)
        {
            const float pier = 0.55f;
            int bays = Mathf.Max(4, Mathf.RoundToInt(halfWidth / 2.2f));

            for (int i = 0; i <= bays; i++)
            {
                float x = Mathf.Lerp(-halfWidth + pier, halfWidth - pier, i / (float)bays);
                BuildingMeshes.AddBox(buffer, place, wall, x, 0f, halfDepth - pier, pier, height, pier);
            }

            // The back wall of the arcade, set well in, so there is something behind the piers rather
            // than a view straight through the building.
            BuildingMeshes.AddBox(buffer, place, wall, 0f, 0f, -halfDepth * 0.15f,
                halfWidth, height, halfDepth * 0.85f);

            // Doorway into the hall, in the middle bay.
            BuildingMeshes.AddBox(buffer, place, trim, 0f, 0f, halfDepth * 0.7f + 0.04f,
                1.35f, height * 0.72f, 0.08f);

            // Two flanking piers on each side wall, so the colonnade turns the corner.
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                BuildingMeshes.AddBox(buffer, place, wall, sign * (halfWidth - pier), 0f,
                    halfDepth * 0.35f, pier, height, pier);
            }
        }

        /// <summary>A row of tall openings across the front and the flanks of a civic block.</summary>
        private static void AddCivicWindows(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float halfWidth,
            float halfDepth,
            float sillY,
            int count,
            ref PlantRandom random)
        {
            const float half = 0.62f;
            const float height = 2.4f;
            float front = halfDepth + 0.02f;

            // A town hall is lit late and generously — it is the one building on the square that is
            // supposed to look as though somebody is still working in it.
            for (int i = 0; i < count; i++)
            {
                float x = Mathf.Lerp(-halfWidth * 0.72f, halfWidth * 0.72f, i / (float)(count - 1));

                buffer.AddQuadFacing(
                    BuildingMeshes.GlassSubmesh(ref random, 0.75f),
                    place.ToWorld(x - half, sillY, front), place.ToWorld(x + half, sillY, front),
                    place.ToWorld(x + half, sillY + height, front),
                    place.ToWorld(x - half, sillY + height, front),
                    place.Forward);

                BuildingMeshes.AddBox(buffer, place, BuildingMeshes.TrimSubmesh,
                    x, sillY - 0.14f, front, half + 0.14f, 0.12f, 0.12f);
            }
        }

        /// <summary>
        /// A hipped roof: four slopes meeting at a ridge that is shorter than the building.
        ///
        /// Every other roof in this town is gabled, so the two hips at the ends are the cheapest possible
        /// way of saying that this building is not a house.
        /// </summary>
        private static void AddHippedRoof(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            float halfWidth,
            float halfDepth,
            float eaveHeight,
            float ridgeHeight,
            float overhang)
        {
            float ex = halfWidth + overhang;
            float ez = halfDepth + overhang;
            float ridgeHalf = halfWidth * 0.45f;

            Vector3 frontLeft = place.ToWorld(-ex, eaveHeight, ez);
            Vector3 frontRight = place.ToWorld(ex, eaveHeight, ez);
            Vector3 backLeft = place.ToWorld(-ex, eaveHeight, -ez);
            Vector3 backRight = place.ToWorld(ex, eaveHeight, -ez);
            Vector3 ridgeLeft = place.ToWorld(-ridgeHalf, ridgeHeight, 0f);
            Vector3 ridgeRight = place.ToWorld(ridgeHalf, ridgeHeight, 0f);

            buffer.AddQuadFacing(submesh, frontLeft, frontRight, ridgeRight, ridgeLeft, place.Up);
            buffer.AddQuadFacing(submesh, ridgeLeft, ridgeRight, backRight, backLeft, place.Up);

            // The two hips.
            buffer.AddTriangleFacing(submesh, frontRight, backRight, ridgeRight, place.Up);
            buffer.AddTriangleFacing(submesh, backLeft, frontLeft, ridgeLeft, place.Up);

            // Undersides, so the eaves are not paper thin from the square below.
            buffer.AddQuadFacing(submesh, ridgeLeft, ridgeRight, frontRight, frontLeft, -place.Up);
            buffer.AddQuadFacing(submesh, backLeft, backRight, ridgeRight, ridgeLeft, -place.Up);
        }

        /// <summary>A gable breaking the front eaves line, with a clock face on it.</summary>
        private static void AddClockGable(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wall,
            int roof,
            int trim,
            float halfDepth,
            float eaveHeight)
        {
            const float half = 2.6f;
            const float rise = 3.4f;
            float z = halfDepth + 0.35f;

            BuildingMeshes.AddBox(buffer, place, wall, 0f, eaveHeight - 1.6f, z - 0.35f, half, 1.6f, 0.4f);

            Vector3 left = place.ToWorld(-half, eaveHeight, z);
            Vector3 right = place.ToWorld(half, eaveHeight, z);
            Vector3 apex = place.ToWorld(0f, eaveHeight + rise, z);
            buffer.AddTriangleFacing(wall, left, right, apex, place.Forward);

            // Two slopes off the apex, back into the main roof.
            Vector3 backLeft = place.ToWorld(-half - 0.3f, eaveHeight + 0.4f, z - 2.4f);
            Vector3 backRight = place.ToWorld(half + 0.3f, eaveHeight + 0.4f, z - 2.4f);
            Vector3 apexOut = place.ToWorld(0f, eaveHeight + rise + 0.25f, z + 0.3f);

            buffer.AddTriangleFacing(roof, backLeft, place.ToWorld(-half - 0.3f, eaveHeight, z + 0.3f), apexOut,
                place.Up);
            buffer.AddTriangleFacing(roof, place.ToWorld(half + 0.3f, eaveHeight, z + 0.3f), backRight, apexOut,
                place.Up);

            // The clock: a disc of trim with a lit face, because a clock that goes dark at night is a
            // hole in the gable.
            AddPrism(buffer, place, trim, 8, 1.12f, 1.12f, eaveHeight + 0.5f, eaveHeight + 0.62f, 0f, z);

            float faceZ = z + 0.14f;
            for (int i = 0; i < 8; i++)
            {
                float step = Mathf.PI * 2f / 8f;
                float a0 = i * step;
                float a1 = (i + 1) * step;

                buffer.AddTriangleFacing(
                    BuildingMeshes.WindowLitSubmesh,
                    place.ToWorld(0f, eaveHeight + 1.55f, faceZ),
                    place.ToWorld(Mathf.Cos(a0) * 0.95f, eaveHeight + 1.55f + Mathf.Sin(a0) * 0.95f, faceZ),
                    place.ToWorld(Mathf.Cos(a1) * 0.95f, eaveHeight + 1.55f + Mathf.Sin(a1) * 0.95f, faceZ),
                    place.Forward);
            }
        }

        /// <summary>
        /// A fountain: an octagonal basin with a pedestal and a bowl. About 110 triangles.
        ///
        /// The one thing in the town with water in it, and the reason a square has a centre at all — an
        /// empty paved rectangle reads as a car park however well it is edged.
        /// </summary>
        public static void AddFountain(
            VegetationMeshBuffer buffer, in PlantPlacement place, ref PlantRandom random)
        {
            const int trim = BuildingMeshes.TrimSubmesh;
            const int sides = 8;

            float radius = random.Range(2.6f, 3.2f);

            // Basin: a low wall with a rim, and the water as a disc set just below it. The water uses the
            // dark glass, which is exactly the right material for it — an unlit near-black that catches
            // nothing, which is what still water in a stylised town looks like.
            AddPrism(buffer, place, trim, sides, radius, radius, -0.2f, 0.62f, 0f, 0f);
            AddPrism(buffer, place, trim, sides, radius - 0.32f, radius - 0.32f, 0f, 0.62f, 0f, 0f);
            AddRingCap(buffer, place, trim, sides, radius - 0.32f, radius, 0.62f);
            AddDisc(buffer, place, BuildingMeshes.WindowDarkSubmesh, sides, radius - 0.34f, 0.42f);

            // Pedestal and bowl.
            AddPrism(buffer, place, trim, sides, 0.62f, 0.42f, 0.3f, 1.9f, 0f, 0f);
            AddPrism(buffer, place, trim, sides, 1.05f, 0.75f, 1.9f, 2.35f, 0f, 0f);
            AddDisc(buffer, place, BuildingMeshes.WindowDarkSubmesh, sides, 0.9f, 2.3f);
            AddPrism(buffer, place, trim, sides, 0.2f, 0.14f, 2.35f, 3.1f, 0f, 0f);
        }

        /// <summary>
        /// A market stall: four posts under a pitched awning, with a counter and crates. About 60
        /// triangles.
        ///
        /// The awning goes in the accent submesh, which is the point of it — a dozen of them scattered
        /// across grey paving is the one place in the town where the accent colour appears in quantity,
        /// and it is what makes the square read as busy from the high street.
        /// </summary>
        public static void AddMarketStall(
            VegetationMeshBuffer buffer, in PlantPlacement place, ref PlantRandom random)
        {
            const int trim = BuildingMeshes.TrimSubmesh;
            const int accent = BuildingMeshes.AccentSubmesh;

            float halfWidth = random.Range(1.5f, 2.1f);
            float halfDepth = random.Range(1.0f, 1.4f);
            float height = random.Range(2.1f, 2.4f);

            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? -halfWidth + 0.1f : halfWidth - 0.1f;
                float z = (i & 2) == 0 ? -halfDepth + 0.1f : halfDepth - 0.1f;
                BuildingMeshes.AddBox(buffer, place, trim, x, 0f, z, 0.07f, height, 0.07f);
            }

            // Counter across the front, and a crate or two on the ground behind it.
            BuildingMeshes.AddBox(buffer, place, trim, 0f, 0.75f, halfDepth - 0.2f,
                halfWidth, 0.18f, 0.36f);

            int crates = random.Chance(0.6f) ? 2 : 1;
            for (int i = 0; i < crates; i++)
            {
                float x = random.Range(-halfWidth * 0.6f, halfWidth * 0.6f);
                float size = random.Range(0.24f, 0.36f);
                BuildingMeshes.AddBox(buffer, place, accent, x, 0f, -halfDepth * 0.4f, size, size * 1.6f, size);
            }

            // Awning: a shallow ridge running across the stall, overhanging the counter.
            float ridge = height + 0.55f;
            float ex = halfWidth + 0.35f;
            float ez = halfDepth + 0.45f;

            Vector3 frontLeft = place.ToWorld(-ex, height, ez);
            Vector3 frontRight = place.ToWorld(ex, height, ez);
            Vector3 backLeft = place.ToWorld(-ex, height, -ez);
            Vector3 backRight = place.ToWorld(ex, height, -ez);
            Vector3 ridgeLeft = place.ToWorld(-ex, ridge, 0f);
            Vector3 ridgeRight = place.ToWorld(ex, ridge, 0f);

            buffer.AddQuadFacing(accent, frontLeft, frontRight, ridgeRight, ridgeLeft, place.Up);
            buffer.AddQuadFacing(accent, ridgeLeft, ridgeRight, backRight, backLeft, place.Up);
            buffer.AddQuadFacing(accent, ridgeLeft, ridgeRight, frontRight, frontLeft, -place.Up);
            buffer.AddQuadFacing(accent, backLeft, backRight, ridgeRight, ridgeLeft, -place.Up);
        }

        /// <summary>A flat annulus closing the top of a two-walled ring, like a basin rim.</summary>
        private static void AddRingCap(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float innerRadius,
            float outerRadius,
            float y)
        {
            float step = Mathf.PI * 2f / sides;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;

                // Walked backwards round the ring, for the same reason the lamp pools are: the placement
                // basis has Cross(Right, Forward) pointing *down*, so a ring traversed in increasing
                // angle and closed against up comes out facing the ground. The flip counter said so, at
                // exactly two triangles per facet.
                buffer.AddQuadFacing(
                    submesh,
                    Ring(place, a1, innerRadius, y, 0f, 0f),
                    Ring(place, a1, outerRadius, y, 0f, 0f),
                    Ring(place, a0, outerRadius, y, 0f, 0f),
                    Ring(place, a0, innerRadius, y, 0f, 0f),
                    place.Up);
            }
        }

        /// <summary>A flat n-gon facing up — a water surface, or the bottom of a bowl.</summary>
        private static void AddDisc(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            int sides,
            float radius,
            float y)
        {
            float step = Mathf.PI * 2f / sides;
            Vector3 centre = place.ToWorld(0f, y, 0f);

            // Backwards round the ring — see AddRingCap.
            for (int i = 0; i < sides; i++)
            {
                buffer.AddTriangleFacing(
                    submesh,
                    centre,
                    Ring(place, (i + 1) * step, radius, y, 0f, 0f),
                    Ring(place, i * step, radius, y, 0f, 0f),
                    place.Up);
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
