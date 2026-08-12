using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The things a village is made of — houses, fences, hedges, lamps and parked cars — built from boxes,
    /// pitched roofs and recessed openings.
    ///
    /// Deliberately the same shape of code as <see cref="PlantMeshes"/>, and it reuses that file's
    /// <see cref="VegetationMeshBuffer"/> and <see cref="PlantPlacement"/> rather than introducing a
    /// second way of doing the same thing. That gets flat shading, unshared vertices, merged per-tile
    /// meshes, submesh compaction and hashed per-instance variation for free, and it means a village mesh
    /// and a vegetation mesh are the same kind of object to everything downstream.
    ///
    /// Everything is authored in the placement's local frame: +X right, +Y up, +Z towards the street.
    /// The placement carries the world position and yaw, so a house is written once facing forward and
    /// lands turned to face whatever road it belongs to.
    ///
    /// <para>Every face here goes through <see cref="VegetationMeshBuffer.AddQuadFacing"/> with the
    /// direction it is meant to look in, rather than being wound by hand. That is not ceremony: houses were
    /// rendering as open dollhouses because <see cref="PlantPlacement"/>'s basis was mirrored, and while
    /// un-mirroring it fixed the boxes, it also turned up two helpers — the flank window and the wing's
    /// lean-to — that had been inside out on one side each since they were written, in a way no frame
    /// convention could have fixed. A face that states where it looks cannot have that bug.</para>
    /// </summary>
    public static class BuildingMeshes
    {
        /// <summary>
        /// The façade palette, as colours rather than as materials.
        ///
        /// <para><b>This used to be twelve materials and is now one.</b> A per-house tint had to be a
        /// per-house material, because URP/Lit cannot read vertex colours — and a material is a submesh,
        /// and a submesh is a draw call. A town tile carried twelve, which <c>ReportDrawCallBudget</c>
        /// reported as being over what a mid-range Android will hold. <c>Horizon/VertexTintLit</c> is
        /// sixty lines of HLSL whose only unusual line multiplies the base colour by the vertex colour,
        /// and with it the whole palette rides in the mesh and every opaque face of a building is one
        /// draw call.</para>
        ///
        /// <para>The numbers are the ones the materials had, moved rather than re-chosen, so the town
        /// comes out the colour it already was.</para>
        ///
        /// <para><b>Three walls, not four, and that is still the opinionated part.</b> Three walls against
        /// three roofs is nine combinations, and because a run of houses shares its colours the street
        /// reads as varied at the scale of runs — which is the scale you see from a car. The constraint
        /// that made a fourth expensive is gone; the judgement that nine is enough is not.</para>
        /// </summary>
        public static readonly Color[] WallColours =
        {
            new Color(0.87f, 0.83f, 0.75f),
            new Color(0.91f, 0.86f, 0.70f),
            new Color(0.80f, 0.68f, 0.53f),
        };

        public static readonly Color[] RoofColours =
        {
            new Color(0.44f, 0.23f, 0.18f),
            new Color(0.31f, 0.30f, 0.32f),
            new Color(0.55f, 0.32f, 0.20f),
        };

        /// <summary>Doors, sills, fence posts, beams, lamp posts, timber.</summary>
        public static readonly Color TrimColour = new Color(0.38f, 0.31f, 0.25f);

        /// <summary>Hedges and garden planting. The undergrowth green, so a hedge matches a bush.</summary>
        public static readonly Color GardenColour = new Color(0.32f, 0.44f, 0.22f);

        /// <summary>
        /// The painted colour: shutters, canopies, balcony rails.
        ///
        /// One recurring saturated colour across a façade of plaster and stone does more for a street than
        /// another plaster tone would, because it is the only thing in the palette that is *not* a
        /// building material.
        /// </summary>
        public static readonly Color AccentColour = new Color(0.26f, 0.36f, 0.33f);

        /// <summary>Glass that never lights, by day and by night alike.</summary>
        public static readonly Color WindowDarkColour = new Color(0.20f, 0.23f, 0.27f);

        public const int WallVariants = 3;

        public const int RoofVariants = 3;

        public const int FirstWallSubmesh = 0;
        public const int FirstRoofSubmesh = FirstWallSubmesh + WallVariants;

        /// <summary>Glass that never lights. Most windows, and every parked car.</summary>
        public const int WindowDarkSubmesh = FirstRoofSubmesh + RoofVariants;

        /// <summary>
        /// Glass that swaps to the lit material at dusk. See <see cref="GlassSubmesh"/>.
        ///
        /// One of only two submeshes left outside <see cref="TintedSubmesh"/>, and for a reason a vertex
        /// colour cannot cover: <c>TownLights</c> swaps the whole material after sunset, and a swap is
        /// per slot. A tint baked into the mesh is a tint for good.
        /// </summary>
        public const int WindowLitSubmesh = WindowDarkSubmesh + 1;

        /// <summary>
        /// Lantern heads and the pools of light under them.
        ///
        /// Its own submesh rather than sharing <see cref="WindowLitSubmesh"/> because a lamp wants a
        /// brighter, whiter night material than a house window, and by day it wants to be the street
        /// rather than dark glass. Two different pairs of materials is two groups, which is one submesh.
        /// </summary>
        public const int LampLitSubmesh = WindowLitSubmesh + 1;

        /// <summary>Doors, sills, fence posts, beams, lamp posts, timber.</summary>
        public const int TrimSubmesh = LampLitSubmesh + 1;

        /// <summary>Hedges and garden planting.</summary>
        public const int GardenSubmesh = TrimSubmesh + 1;

        /// <summary>The painted colour: shutters, canopies, balcony rails.</summary>
        public const int AccentSubmesh = GardenSubmesh + 1;

        /// <summary>
        /// Twelve categories a face can belong to — but no longer twelve draw calls.
        ///
        /// <para>These stay twelve because they are what a <i>builder</i> means: a roof tile is not a
        /// shutter, and saying so at the point the face is written is how the palette gets applied at
        /// all. What changed is the other end. Ten of the twelve are merged into one submesh with their
        /// colour written into the vertices — see <see cref="OpaqueTints"/> and
        /// <c>VegetationMeshBuffer.MergeTinted</c> — so a town tile now costs three draw calls where it
        /// cost twelve, and adding a thirteenth category costs a colour rather than a call.</para>
        ///
        /// <para>Merging at the mesh rather than at the fifty-odd call sites is deliberate. Several
        /// builders cache their submesh in a <c>const</c> and reuse it down the method; a scheme that
        /// needed the tint set immediately before every emit would have had to unpick all of them, for
        /// a mesh that comes out identical either way.</para>
        /// </summary>
        public const int SubmeshCount = AccentSubmesh + 1;

        /// <summary>
        /// The colour each submesh is tinted with when the opaque ones are merged, or null where a
        /// submesh must keep its own material.
        ///
        /// <para>The two nulls are <see cref="WindowLitSubmesh"/> and <see cref="LampLitSubmesh"/>, and
        /// they are not an oversight: <c>TownLights</c> swaps their whole material after sunset, and a
        /// tint baked into a mesh cannot be swapped. Everything else in a building is one colour for
        /// good, which is exactly what a vertex colour is.</para>
        /// </summary>
        public static Color?[] OpaqueTints()
        {
            var tints = new Color?[SubmeshCount];

            for (int i = 0; i < WallVariants; i++)
            {
                tints[FirstWallSubmesh + i] = WallColours[i];
            }

            for (int i = 0; i < RoofVariants; i++)
            {
                tints[FirstRoofSubmesh + i] = RoofColours[i];
            }

            tints[WindowDarkSubmesh] = WindowDarkColour;
            tints[TrimSubmesh] = TrimColour;
            tints[GardenSubmesh] = GardenColour;
            tints[AccentSubmesh] = AccentColour;

            return tints;
        }

        /// <summary>Wall submesh for one of the palette variants.</summary>
        public static int WallSubmesh(int variant)
        {
            return FirstWallSubmesh + Mathf.Abs(variant) % WallVariants;
        }

        /// <summary>Roof submesh for one of the palette variants.</summary>
        public static int RoofSubmesh(int variant)
        {
            return FirstRoofSubmesh + Mathf.Abs(variant) % RoofVariants;
        }

        /// <summary>
        /// Which of the two glass submeshes a pane goes in: lit after dark, or never.
        ///
        /// <para>The draw comes from the <i>building's own</i> <see cref="PlantRandom"/> stream, so a house
        /// lights the same windows on every rebuild. It also means adding this call shifted every
        /// subsequent draw in <see cref="AddHouse"/> — every house in the town changed shape and colour on
        /// the run that introduced it. Deterministic and intended; it is only worth stating so a wholesale
        /// visual change is not mistaken for a bug.</para>
        /// </summary>
        public static int GlassSubmesh(ref PlantRandom random, float litChance)
        {
            return random.Chance(litChance) ? WindowLitSubmesh : WindowDarkSubmesh;
        }

        /// <summary>How deep a window sits back into the wall. This is what gives a façade a shadow.</summary>
        private const float Reveal = 0.16f;

        /// <summary>Floor-to-floor height in the city, metres.</summary>
        private const float Storey = 3.4f;

        /// <summary>
        /// A city tower: podium, shaft with a setback or two, parapet, and a band of glass per storey.
        ///
        /// <para><b>Why a tower is not a scaled-up house.</b> <see cref="AddHouse"/> is a gable-roofed
        /// recipe with a door and a window grid — enlarging it gives a very big cottage. What reads as a
        /// tower from a car is three things: a flat top with a parapet, horizontal banding rather than
        /// punched openings, and a silhouette that steps back as it rises. That is all this builds.</para>
        ///
        /// <para><b>The bands must be glass submeshes.</b> Each band rolls
        /// <see cref="GlassSubmesh"/> independently per face per storey, which is what gives a night
        /// skyline its chequer — and it only works because those two submeshes are the ones
        /// <see cref="OpaqueTints"/> leaves out of the tinted merge, so <c>TownLights</c> can still swap
        /// their material at dusk. Put a band anywhere else and the tower is dark all night.</para>
        ///
        /// <para>Roughly 200 triangles at twenty storeys, and no draw call of its own: it lands in the
        /// same three the town already pays.</para>
        /// </summary>
        public static void AddTower(VegetationMeshBuffer buffer, in PlantPlacement place, float litChance)
        {
            var random = new PlantRandom(place.Seed);

            int wall = WallSubmesh((int)(place.Seed % WallVariants));

            float halfWidth = random.Range(9f, 13f);
            float halfDepth = random.Range(8f, 11f);

            // Height carries the skyline, so it is drawn over a wide range and skewed tall — squaring a
            // 0..1 roll and reading it upwards makes most towers middling and a few genuinely dominant,
            // which is the shape of a real skyline. A uniform roll gives a row of near-identical slabs.
            float tall = random.Range(0f, 1f);
            int storeys = Mathf.RoundToInt(Mathf.Lerp(7f, 23f, 1f - tall * tall));

            // Podium: full footprint, taller floors, and it is what the street actually sees.
            const int podiumStoreys = 2;
            float podiumHeight = podiumStoreys * Storey * 1.25f;

            AddBox(buffer, place, wall, 0f, 0f, 0f, halfWidth, podiumHeight, halfDepth);
            AddGlassBands(buffer, place, 0f, 0f, halfWidth, halfDepth,
                podiumStoreys, Storey * 1.25f, litChance, ref random);

            // A lip over the podium, so the shaft does not simply continue out of it.
            AddBox(buffer, place, TrimSubmesh, 0f, podiumHeight, 0f,
                halfWidth + 0.25f, 0.35f, halfDepth + 0.25f);

            float baseY = podiumHeight + 0.35f;
            int remaining = storeys;
            int setbacks = random.Chance(0.55f) ? 2 : 1;

            for (int stage = 0; stage < setbacks && remaining > 0; stage++)
            {
                bool last = stage == setbacks - 1;
                int stageStoreys = last ? remaining : Mathf.Max(2, remaining / 2);
                remaining -= stageStoreys;

                float shrink = stage == 0 ? 0.86f : 0.74f;
                float stageHalfWidth = halfWidth * shrink;
                float stageHalfDepth = halfDepth * shrink;
                float stageHeight = stageStoreys * Storey;

                AddBox(buffer, place, wall, 0f, baseY, 0f,
                    stageHalfWidth, stageHeight, stageHalfDepth);

                AddGlassBands(buffer, place, baseY, 0f, stageHalfWidth, stageHalfDepth,
                    stageStoreys, Storey, litChance, ref random);

                baseY += stageHeight;

                // Parapet on the last stage only; the others are hidden by the stage above.
                if (last)
                {
                    AddBox(buffer, place, TrimSubmesh, 0f, baseY, 0f,
                        stageHalfWidth + 0.2f, 0.9f, stageHalfDepth + 0.2f);
                    AddRoofPlant(buffer, place, baseY, stageHalfWidth, stageHalfDepth, ref random);
                }
            }
        }

        /// <summary>
        /// A perimeter block: a continuous street wall with a shopfront at the bottom and a flat top.
        ///
        /// <para>Wide and shallow rather than square, because what it is for is to stand shoulder to
        /// shoulder with its neighbours and make a street out of them. The <c>Commercial</c> quarter's
        /// spacing is barely wider than this, which is what closes the wall up.</para>
        /// </summary>
        public static void AddPerimeterBlock(
            VegetationMeshBuffer buffer, in PlantPlacement place, float litChance)
        {
            var random = new PlantRandom(place.Seed);

            int wall = WallSubmesh((int)(place.Seed % WallVariants));

            float halfWidth = random.Range(11f, 13.5f);
            float halfDepth = random.Range(7f, 9.5f);
            int storeys = random.Range(0f, 1f) < 0.35f ? 6 : random.Range(0f, 1f) < 0.6f ? 5 : 4;

            const float groundHeight = 4.4f;
            float upperHeight = (storeys - 1) * Storey;

            // The shopfront: its own band, mostly glass, and set slightly proud so the storeys above
            // read as sitting on it rather than as starting at the pavement.
            AddBox(buffer, place, TrimSubmesh, 0f, 0f, 0f,
                halfWidth + 0.2f, groundHeight, halfDepth + 0.2f);
            AddGlassBands(buffer, place, 0f, 0.25f, halfWidth + 0.2f, halfDepth + 0.2f,
                1, groundHeight, litChance, ref random);

            AddBox(buffer, place, wall, 0f, groundHeight, 0f, halfWidth, upperHeight, halfDepth);
            AddGlassBands(buffer, place, groundHeight, 0f, halfWidth, halfDepth,
                storeys - 1, Storey, litChance, ref random);

            // Cornice and parapet. Two thin boxes are what stop a flat-topped block reading as an
            // extruded rectangle, and they cost twenty triangles.
            AddBox(buffer, place, AccentSubmesh, 0f, groundHeight + upperHeight, 0f,
                halfWidth + 0.35f, 0.4f, halfDepth + 0.35f);
            AddBox(buffer, place, TrimSubmesh, 0f, groundHeight + upperHeight + 0.4f, 0f,
                halfWidth + 0.1f, 0.7f, halfDepth + 0.1f);
        }

        /// <summary>
        /// One horizontal band of glass per storey, on all four faces, each rolled for lit or dark
        /// independently.
        ///
        /// <para>Standing 4 cm proud of the wall rather than inset. A recess would be truer to a real
        /// façade and would need its own reveal geometry on every band — four extra quads a storey for a
        /// shadow line nobody resolves from a moving car. Proud is one quad and never z-fights.</para>
        /// </summary>
        private static void AddGlassBands(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float baseY,
            float inset,
            float halfWidth,
            float halfDepth,
            int storeys,
            float storeyHeight,
            float litChance,
            ref PlantRandom random)
        {
            const float proud = 0.04f;
            float bandHeight = storeyHeight * 0.55f;

            float x = halfWidth - inset + proud;
            float z = halfDepth - inset + proud;

            for (int i = 0; i < storeys; i++)
            {
                float y0 = baseY + i * storeyHeight + storeyHeight * 0.28f;
                float y1 = y0 + bandHeight;

                AddBandFace(buffer, place, GlassSubmesh(ref random, litChance),
                    -halfWidth * 0.86f, halfWidth * 0.86f, y0, y1, z, true);
                AddBandFace(buffer, place, GlassSubmesh(ref random, litChance),
                    -halfWidth * 0.86f, halfWidth * 0.86f, y0, y1, -z, false);
                AddBandFace(buffer, place, GlassSubmesh(ref random, litChance),
                    -halfDepth * 0.86f, halfDepth * 0.86f, y0, y1, x, true, sideways: true);
                AddBandFace(buffer, place, GlassSubmesh(ref random, litChance),
                    -halfDepth * 0.86f, halfDepth * 0.86f, y0, y1, -x, false, sideways: true);
            }
        }

        private static void AddBandFace(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int submesh,
            float from,
            float to,
            float y0,
            float y1,
            float offset,
            bool positive,
            bool sideways = false)
        {
            Vector3 a, b, c, d;

            if (sideways)
            {
                a = place.ToWorld(offset, y0, from);
                b = place.ToWorld(offset, y0, to);
                c = place.ToWorld(offset, y1, to);
                d = place.ToWorld(offset, y1, from);
            }
            else
            {
                a = place.ToWorld(from, y0, offset);
                b = place.ToWorld(to, y0, offset);
                c = place.ToWorld(to, y1, offset);
                d = place.ToWorld(from, y1, offset);
            }

            Vector3 outward = sideways
                ? (positive ? place.Right : -place.Right)
                : (positive ? place.Forward : -place.Forward);

            buffer.AddQuadFacing(submesh, a, b, c, d, outward);
        }

        /// <summary>Lift housing and a mast. The thing that stops every tower ending in the same flat line.</summary>
        private static void AddRoofPlant(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float baseY,
            float halfWidth,
            float halfDepth,
            ref PlantRandom random)
        {
            float boxHalfWidth = halfWidth * random.Range(0.3f, 0.5f);
            float boxHalfDepth = halfDepth * random.Range(0.3f, 0.5f);
            float offsetX = random.Range(-halfWidth * 0.3f, halfWidth * 0.3f);

            AddBox(buffer, place, TrimSubmesh, offsetX, baseY, 0f,
                boxHalfWidth, random.Range(2.2f, 3.6f), boxHalfDepth);

            if (random.Chance(0.45f))
            {
                AddBox(buffer, place, AccentSubmesh, offsetX, baseY + 3.6f, 0f,
                    0.18f, random.Range(5f, 11f), 0.18f);
            }
        }

        /// <summary>
        /// A detached house: plinth, walls, a pitched roof with eaves, a door and a grid of windows.
        /// About 70 triangles.
        /// </summary>
        /// <param name="litChance">
        /// Fraction of this building's panes that light after dark. Comes down from the quarter the plot
        /// stands in — a housing street is sparser than a high street of shopfronts — rather than being a
        /// constant here, because "how much of this street is awake at night" is a property of the street.
        /// </param>
        public static void AddHouse(VegetationMeshBuffer buffer, in PlantPlacement place, float litChance)
        {
            var random = new PlantRandom(place.Seed);

            int wall = WallSubmesh((int)(place.Seed % WallVariants));
            int roof = RoofSubmesh((int)((place.Seed >> 8) % RoofVariants));

            float halfWidth = random.Range(3.6f, 5.4f);
            float halfDepth = random.Range(3.2f, 4.6f);
            float eaveHeight = random.Range(2.7f, 3.4f);
            bool twoStorey = random.Chance(0.45f);
            if (twoStorey)
            {
                eaveHeight += random.Range(2.2f, 2.8f);
            }

            float ridgeHeight = eaveHeight + random.Range(1.9f, 3.0f);
            const float overhang = 0.4f;

            // Plinth, so the house meets the ground on a base rather than on a cut line. Sunk below grade
            // so an uneven garden cannot show daylight under a wall.
            AddBox(buffer, place, wall, 0f, -0.4f, 0f, halfWidth + 0.14f, 0.75f, halfDepth + 0.14f);
            AddBox(buffer, place, wall, 0f, 0f, 0f, halfWidth, eaveHeight, halfDepth);
            AddGableRoof(buffer, place, wall, roof, halfWidth, halfDepth, eaveHeight, ridgeHeight, overhang);

            // A storey band where the floors change. It reads as a course of stone and it is the cheapest
            // thing that stops a two-storey wall being one flat plane four metres tall.
            if (twoStorey)
            {
                AddBox(buffer, place, TrimSubmesh, 0f, eaveHeight - 2.75f, 0f,
                    halfWidth + 0.06f, 0.16f, halfDepth + 0.06f);
            }

            float doorX = random.Range(-halfWidth * 0.45f, halfWidth * 0.45f);
            AddDoorway(buffer, place, doorX, halfDepth, ref random);

            AddWindowRow(buffer, place, wall, halfWidth, halfDepth, 1.1f, doorX, litChance, ref random);
            if (twoStorey)
            {
                AddWindowRow(buffer, place, wall, halfWidth, halfDepth, eaveHeight - 2.0f,
                    float.MaxValue, litChance, ref random);
            }

            // The gable used to be a blank triangle on every single house. A loft is the least likely
            // room in the house to be lit, so it takes a fraction of the building's chance rather than
            // all of it.
            AddWindowOpening(buffer, place, wall, doorX * 0.2f, eaveHeight + 0.5f, halfDepth,
                0.42f, 0.7f, false, litChance * 0.4f, ref random);

            if (random.Chance(0.55f))
            {
                AddDormer(buffer, place, wall, roof, halfDepth, eaveHeight, ridgeHeight, litChance,
                    ref random);
            }

            if (random.Chance(0.4f))
            {
                AddWing(buffer, place, wall, roof, halfWidth, halfDepth, litChance, ref random);
            }

            if (twoStorey && random.Chance(0.35f))
            {
                AddBalcony(buffer, place, halfWidth, halfDepth, eaveHeight - 2.3f);
            }

            if (random.Chance(0.75f))
            {
                float chimneyX = halfWidth * random.Range(-0.6f, 0.6f);
                AddBox(buffer, place, wall, chimneyX, ridgeHeight - 0.7f, 0f, 0.3f, 1.5f, 0.3f);
                AddBox(buffer, place, TrimSubmesh, chimneyX, ridgeHeight + 0.72f, 0f, 0.36f, 0.16f, 0.36f);
            }
        }

        /// <summary>
        /// A window set back into the wall, with a sill and a pair of shutters.
        ///
        /// This is what "façade" means here. The old window was two triangles of flat colour offset 3 cm
        /// from the wall — at any distance it read as a sticker, because that is what it was. A reveal
        /// catches a shadow on one side and light on the other, and that alone does more for a wall than
        /// any amount of extra colour.
        /// </summary>
        private static void AddWindowOpening(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wallSubmesh,
            float centerX,
            float baseY,
            float wallZ,
            float halfWidth,
            float height,
            bool shutters,
            float litChance,
            ref PlantRandom random)
        {
            float x0 = centerX - halfWidth;
            float x1 = centerX + halfWidth;
            float y1 = baseY + height;
            float back = wallZ - Reveal;

            // The four sides of the recess, in the wall colour so they read as the wall's own thickness.
            // A reveal looks *inwards*, across the opening — that is the whole point of a reveal, and
            // stating it is what stops the jambs being wound by accident.
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(x0, baseY, wallZ), place.ToWorld(x0, baseY, back),
                place.ToWorld(x0, y1, back), place.ToWorld(x0, y1, wallZ), place.Right);
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(x1, baseY, back), place.ToWorld(x1, baseY, wallZ),
                place.ToWorld(x1, y1, wallZ), place.ToWorld(x1, y1, back), -place.Right);
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(x0, y1, wallZ), place.ToWorld(x0, y1, back),
                place.ToWorld(x1, y1, back), place.ToWorld(x1, y1, wallZ), -place.Up);
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(x0, baseY, back), place.ToWorld(x0, baseY, wallZ),
                place.ToWorld(x1, baseY, wallZ), place.ToWorld(x1, baseY, back), place.Up);

            // The glass itself, at the back of the recess.
            buffer.AddQuadFacing(GlassSubmesh(ref random, litChance),
                place.ToWorld(x0, baseY, back), place.ToWorld(x1, baseY, back),
                place.ToWorld(x1, y1, back), place.ToWorld(x0, y1, back), place.Forward);

            // Sill, projecting past the opening on both sides.
            AddBox(buffer, place, TrimSubmesh, centerX, baseY - 0.1f, wallZ + 0.04f,
                halfWidth + 0.1f, 0.09f, 0.09f);

            if (!shutters)
            {
                return;
            }

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                AddBox(buffer, place, AccentSubmesh, centerX + sign * (halfWidth + halfWidth * 0.5f),
                    baseY, wallZ + 0.05f, halfWidth * 0.5f, height, 0.05f);
            }
        }

        /// <summary>A door in a surround, with a step and a small canopy over it.</summary>
        private static void AddDoorway(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float centerX,
            float wallZ,
            ref PlantRandom random)
        {
            const float halfWidth = 0.52f;
            const float height = 2.1f;
            float back = wallZ - Reveal * 0.7f;

            buffer.AddQuadFacing(TrimSubmesh,
                place.ToWorld(centerX - halfWidth, 0f, back), place.ToWorld(centerX + halfWidth, 0f, back),
                place.ToWorld(centerX + halfWidth, height, back), place.ToWorld(centerX - halfWidth, height, back),
                place.Forward);

            // Frame: two jambs and a lintel standing proud of the wall.
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                AddBox(buffer, place, TrimSubmesh, centerX + sign * (halfWidth + 0.07f), 0f, wallZ,
                    0.07f, height + 0.14f, 0.07f);
            }

            AddBox(buffer, place, TrimSubmesh, centerX, height, wallZ, halfWidth + 0.14f, 0.14f, 0.07f);

            // Step and canopy.
            AddBox(buffer, place, TrimSubmesh, centerX, -0.16f, wallZ + 0.3f, halfWidth + 0.2f, 0.2f, 0.32f);

            // The canopy is the awning of the accent palette: painted, and the one piece of colour that
            // sits at eye level right where a driver passes the front of the house.
            if (random.Chance(0.6f))
            {
                AddBox(buffer, place, AccentSubmesh, centerX, height + 0.28f, wallZ + 0.28f,
                    halfWidth + 0.3f, 0.1f, 0.36f);
            }
        }

        /// <summary>A dormer poking out of the street-facing roof slope.</summary>
        private static void AddDormer(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wallSubmesh,
            int roofSubmesh,
            float halfDepth,
            float eaveHeight,
            float ridgeHeight,
            float litChance,
            ref PlantRandom random)
        {
            const float halfWidth = 0.85f;
            float centerX = random.Range(-1.4f, 1.4f);

            // Sat halfway up the slope, its face flush with where the roof is at that depth.
            float t = 0.45f;
            float z = Mathf.Lerp(halfDepth, 0f, t);
            float baseY = Mathf.Lerp(eaveHeight, ridgeHeight, t) - 0.35f;
            float height = 1.35f;

            AddBox(buffer, place, wallSubmesh, centerX, baseY, z, halfWidth, height, 0.9f);
            AddWindowOpening(buffer, place, wallSubmesh, centerX, baseY + 0.35f, z + 0.9f,
                halfWidth * 0.6f, 0.75f, false, litChance, ref random);

            // Its own little pitched roof.
            Vector3 left = place.ToWorld(centerX - halfWidth - 0.15f, baseY + height, z + 1.0f);
            Vector3 right = place.ToWorld(centerX + halfWidth + 0.15f, baseY + height, z + 1.0f);
            Vector3 ridgeL = place.ToWorld(centerX - halfWidth - 0.15f, baseY + height + 0.55f, z - 0.9f);
            Vector3 ridgeR = place.ToWorld(centerX + halfWidth + 0.15f, baseY + height + 0.55f, z - 0.9f);

            buffer.AddQuadFacing(roofSubmesh, left, right, ridgeR, ridgeL, place.Up);
        }

        /// <summary>A lower wing off one flank, so the plan is an L rather than a shoebox.</summary>
        private static void AddWing(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wallSubmesh,
            int roofSubmesh,
            float halfWidth,
            float halfDepth,
            float litChance,
            ref PlantRandom random)
        {
            float sign = random.Chance(0.5f) ? 1f : -1f;
            float wingHalfWidth = random.Range(1.5f, 2.3f);
            float wingHalfDepth = halfDepth * random.Range(0.5f, 0.7f);
            float wingHeight = random.Range(2.3f, 2.8f);
            float centerX = sign * (halfWidth + wingHalfWidth - 0.2f);
            float centerZ = random.Range(-halfDepth * 0.3f, halfDepth * 0.2f);

            AddBox(buffer, place, wallSubmesh, centerX, -0.3f, centerZ,
                wingHalfWidth + 0.1f, 0.55f, wingHalfDepth + 0.1f);
            AddBox(buffer, place, wallSubmesh, centerX, 0f, centerZ, wingHalfWidth, wingHeight, wingHalfDepth);

            // A lean-to, sloping away from the main house.
            Vector3 high = place.ToWorld(centerX - sign * wingHalfWidth, wingHeight + 0.7f, centerZ);
            Vector3 low = place.ToWorld(centerX + sign * (wingHalfWidth + 0.25f), wingHeight, centerZ);

            // Same mirror as the flank window: the slope runs the other way on the other flank, so folding
            // `sign` into the depth offset walks the corners the other way round and keeps the winding. The
            // quad is geometrically identical either way — only the order it is traversed in changes.
            Vector3 alongDepth = place.Forward * ((wingHalfDepth + 0.2f) * sign);

            Vector3 highFront = high + alongDepth;
            Vector3 highBack = high - alongDepth;
            Vector3 lowFront = low + alongDepth;
            Vector3 lowBack = low - alongDepth;

            // The pitch is well under 45 degrees either way, so "up" settles it without the slope normal.
            buffer.AddQuadFacing(roofSubmesh, highFront, lowFront, lowBack, highBack, place.Up);

            AddWindowOpening(buffer, place, wallSubmesh, centerX, 0.9f, centerZ + wingHalfDepth,
                0.42f, 0.85f, false, litChance, ref random);
        }

        /// <summary>A small balcony on the street face, with a rail.</summary>
        private static void AddBalcony(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float halfWidth,
            float halfDepth,
            float atY)
        {
            float half = Mathf.Min(1.6f, halfWidth * 0.55f);

            // Slab in trim, rail in the accent colour: the slab is the building, the rail is joinery
            // somebody painted.
            AddBox(buffer, place, TrimSubmesh, 0f, atY, halfDepth + 0.5f, half, 0.12f, 0.55f);
            AddBox(buffer, place, AccentSubmesh, 0f, atY + 0.12f, halfDepth + 1.0f, half, 0.85f, 0.06f);

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                AddBox(buffer, place, AccentSubmesh, sign * half, atY + 0.12f, halfDepth + 0.5f,
                    0.06f, 0.85f, 0.55f);
            }
        }


        /// <summary>A clipped hedge along a plot edge — one long box with a jittered top.</summary>
        public static void AddHedge(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float centerX,
            float centerZ,
            float halfLengthX,
            float halfLengthZ,
            ref PlantRandom random)
        {
            float height = random.Range(0.9f, 1.4f);
            float thickness = random.Range(0.35f, 0.55f);

            float halfX = halfLengthX > 0f ? halfLengthX : thickness;
            float halfZ = halfLengthZ > 0f ? halfLengthZ : thickness;

            AddBox(buffer, place, GardenSubmesh, centerX, -0.15f, centerZ, halfX, height, halfZ);
        }

        /// <summary>A picket fence: a rail plus posts. Runs along X or along Z, whichever half-length is set.</summary>
        public static void AddFence(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            float centerX,
            float centerZ,
            float halfLengthX,
            float halfLengthZ,
            ref PlantRandom random)
        {
            const float height = 1.0f;
            const float postSpacing = 2.2f;
            float thickness = 0.07f;

            bool alongX = halfLengthX > halfLengthZ;
            float halfLength = alongX ? halfLengthX : halfLengthZ;

            float railHalfX = alongX ? halfLength : thickness;
            float railHalfZ = alongX ? thickness : halfLength;

            // Two rails rather than individual pickets: at village scale the rails carry the read, and a
            // picket each 12 cm would cost more triangles than the house behind it.
            AddBox(buffer, place, TrimSubmesh, centerX, height * 0.55f, centerZ, railHalfX, 0.10f, railHalfZ);
            AddBox(buffer, place, TrimSubmesh, centerX, height * 0.15f, centerZ, railHalfX, 0.09f, railHalfZ);

            int posts = Mathf.Max(2, Mathf.FloorToInt(halfLength * 2f / postSpacing) + 1);
            for (int i = 0; i < posts; i++)
            {
                float t = Mathf.Lerp(-halfLength, halfLength, i / (float)(posts - 1));
                float x = alongX ? centerX + t : centerX;
                float z = alongX ? centerZ : centerZ + t;

                AddBox(buffer, place, TrimSubmesh, x, -0.1f, z, 0.09f, height + random.Range(0f, 0.08f), 0.09f);
            }
        }

        /// <summary>A street lamp. The head goes in the lamp submesh, so it lights earlier and whiter
        /// than the windows around it.</summary>
        public static void AddStreetLamp(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);
            float height = random.Range(4.2f, 4.8f);

            AddBox(buffer, place, TrimSubmesh, 0f, -0.3f, 0f, 0.11f, height, 0.11f);

            // Arm reaching towards the street, with the lantern on its end.
            AddBox(buffer, place, TrimSubmesh, 0f, height - 0.25f, 0.5f, 0.07f, 0.14f, 0.55f);

            // A dark lid standing proud of the glass on every side, and a small foot under it.
            //
            // Not decoration. The lamp material is unlit and clips to white, so the lantern has no
            // shading of any kind — every face is the same flat maximum, and its whole read is its
            // silhouette. Without the lid it is a white rectangle beside a dark post, which at twenty
            // metres looks like a billboard rather than a lamp; the two dark caps are what make the
            // bright part a lantern.
            AddBox(buffer, place, TrimSubmesh, 0f, height - 0.30f, 1.0f, 0.30f, 0.11f, 0.30f);
            AddBox(buffer, place, LampLitSubmesh, 0f, height - 0.72f, 1.0f, 0.22f, 0.42f, 0.22f);
            AddBox(buffer, place, TrimSubmesh, 0f, height - 0.80f, 1.0f, 0.26f, 0.08f, 0.26f);
        }

        /// <summary>
        /// The pool of light a lamp throws on the carriageway: a flat polygon in the lamp submesh, which
        /// by day carries the road's own material and disappears.
        ///
        /// <para><b>This is the entire night-lighting read, and it is what makes zero runtime lights
        /// affordable.</b> The mobile renderer allows four additional lights per object with no shadows;
        /// a hundred point lights would dominate the frame on a tile GPU for a warm patch on the tarmac,
        /// which is what this is. Sharing <see cref="LampLitSubmesh"/> with the lantern head makes the
        /// pool exactly as bright as the lantern — in flat-shaded stylised rendering that reads fine, and
        /// it saves a submesh.</para>
        ///
        /// <para>The corners arrive in world space already sitting on the street's cross-section, because
        /// a carriageway has a 6 cm crown and a polygon lifted bodily off one height z-fights against it
        /// down the middle of the road. Working out where the surface is belongs to whoever has the
        /// street; all this does is close the polygon.</para>
        /// </summary>
        public static void AddGroundPool(
            VegetationMeshBuffer buffer, IReadOnlyList<Vector3> corners, int start, int count)
        {
            if (corners == null || count < 3 || start + count > corners.Count)
            {
                return;
            }

            var centre = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                centre += corners[start + i];
            }

            centre /= count;

            // Walked backwards round the ring. The corners are laid out with across on the cosine and
            // along on the sine, and up is Cross(forward, right) — so a fan taken in increasing angle
            // comes out facing the ground, and the flip counter said so, 6 triangles per pool exactly.
            for (int i = 0; i < count; i++)
            {
                buffer.AddTriangleFacing(
                    LampLitSubmesh,
                    centre,
                    corners[start + (i + 1) % count],
                    corners[start + i],
                    Vector3.up);
            }
        }

        /// <summary>A parked car — a plain two-box silhouette. Read at ten metres, not at one.</summary>
        public static void AddParkedCar(VegetationMeshBuffer buffer, in PlantPlacement place)
        {
            var random = new PlantRandom(place.Seed);

            float halfLength = random.Range(2.0f, 2.4f);
            const float halfWidth = 0.85f;

            // Dark glass, never lit: a parked car with its cabin glowing is a car with someone sitting
            // in it, which is a different and much odder thing to put on a street.
            AddBox(buffer, place, TrimSubmesh, 0f, 0.35f, 0f, halfWidth, 0.75f, halfLength);
            AddBox(buffer, place, WindowDarkSubmesh, 0f, 1.1f, -0.15f,
                halfWidth * 0.86f, 0.6f, halfLength * 0.52f);
        }

        /// <summary>
        /// An axis-aligned box in the placement's local frame, with a top and a bottom.
        ///
        /// The project already had two oriented boxes — <c>TunnelBuilder.AddBox</c> for gallery pillars and
        /// <c>GuardRailBuilder.AddRectangularTube</c> — and neither is usable here: both are private to
        /// another class, and both emit only four side faces because their ends are always buried. A house
        /// needs its lid.
        /// </summary>
        public static void AddBox(
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

            buffer.AddQuadFacing(submesh, d, c, g, h, place.Forward);    // +Z, the street face
            buffer.AddQuadFacing(submesh, b, a, e, f, -place.Forward);   // -Z
            buffer.AddQuadFacing(submesh, c, b, f, g, place.Right);      // +X
            buffer.AddQuadFacing(submesh, a, d, h, e, -place.Right);     // -X
            buffer.AddQuadFacing(submesh, h, g, f, e, place.Up);         // top
            buffer.AddQuadFacing(submesh, b, c, d, a, -place.Up);        // bottom
        }

        /// <summary>
        /// A pitched roof with its ridge running across the street face, plus the two gable triangles that
        /// close the walls under it.
        /// </summary>
        public static void AddGableRoof(
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

            Vector3 eaveFrontLeft = place.ToWorld(-ex, eaveHeight, ez);
            Vector3 eaveFrontRight = place.ToWorld(ex, eaveHeight, ez);
            Vector3 eaveBackLeft = place.ToWorld(-ex, eaveHeight, -ez);
            Vector3 eaveBackRight = place.ToWorld(ex, eaveHeight, -ez);
            Vector3 ridgeLeft = place.ToWorld(-ex, ridgeHeight, 0f);
            Vector3 ridgeRight = place.ToWorld(ex, ridgeHeight, 0f);

            // A roof pitch is never past vertical, so up and down separate the two slopes from their own
            // undersides without needing the exact slope normal.
            buffer.AddQuadFacing(roofSubmesh, eaveFrontLeft, eaveFrontRight, ridgeRight, ridgeLeft, place.Up);
            buffer.AddQuadFacing(roofSubmesh, ridgeLeft, ridgeRight, eaveBackRight, eaveBackLeft, place.Up);

            // Undersides, so the eaves are not paper thin when seen from a garden. Passing the negated
            // direction is what says "deliberately two-sided" rather than "wound wrong".
            buffer.AddQuadFacing(roofSubmesh, ridgeLeft, ridgeRight, eaveFrontRight, eaveFrontLeft, -place.Up);
            buffer.AddQuadFacing(roofSubmesh, eaveBackLeft, eaveBackRight, ridgeRight, ridgeLeft, -place.Up);

            // Gables at the wall plane, not at the eaves, so the overhang reads as an overhang.
            Vector3 gableRightFront = place.ToWorld(halfWidth, eaveHeight, halfDepth);
            Vector3 gableRightBack = place.ToWorld(halfWidth, eaveHeight, -halfDepth);
            Vector3 gableRightTop = place.ToWorld(halfWidth, ridgeHeight, 0f);
            buffer.AddTriangleFacing(wallSubmesh, gableRightBack, gableRightTop, gableRightFront, place.Right);

            Vector3 gableLeftFront = place.ToWorld(-halfWidth, eaveHeight, halfDepth);
            Vector3 gableLeftBack = place.ToWorld(-halfWidth, eaveHeight, -halfDepth);
            Vector3 gableLeftTop = place.ToWorld(-halfWidth, ridgeHeight, 0f);
            buffer.AddTriangleFacing(wallSubmesh, gableLeftFront, gableLeftTop, gableLeftBack, -place.Right);
        }


        /// <summary>A row of windows across the street face and both flanks at one storey height.</summary>
        private static void AddWindowRow(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wallSubmesh,
            float halfWidth,
            float halfDepth,
            float sillY,
            float doorX,
            float litChance,
            ref PlantRandom random)
        {
            const float windowHalfWidth = 0.55f;
            const float windowHeight = 1.15f;

            int across = Mathf.Max(1, Mathf.FloorToInt(halfWidth));
            for (int i = 0; i < across; i++)
            {
                float x = Mathf.Lerp(-halfWidth * 0.62f, halfWidth * 0.62f, across == 1 ? 0.5f : i / (float)(across - 1));

                // Skip the bay the door is standing in.
                if (Mathf.Abs(x - doorX) < 0.95f)
                {
                    continue;
                }

                AddWindowOpening(buffer, place, wallSubmesh, x, sillY, halfDepth,
                    windowHalfWidth, windowHeight, random.Chance(0.5f), litChance, ref random);
            }

            // A flank faces a neighbour's garden rather than the street, and rather less of the house
            // lives on that side. Half the chance, which is enough to keep a row from lighting like a
            // grid seen end-on.
            if (random.Chance(0.85f))
            {
                AddSideWindow(buffer, place, wallSubmesh, halfWidth, sillY, 0f,
                    windowHalfWidth, windowHeight, true, litChance * 0.5f, ref random);
            }

            if (random.Chance(0.85f))
            {
                AddSideWindow(buffer, place, wallSubmesh, -halfWidth, sillY, 0f,
                    windowHalfWidth, windowHeight, false, litChance * 0.5f, ref random);
            }
        }



        /// <summary>
        /// The same recessed window on a flank wall, which faces along X rather than along Z.
        ///
        /// A separate method rather than a general oriented one: the two differ only in which axis is
        /// pinned, and threading a basis through would cost more to read than it saves to write.
        /// </summary>
        private static void AddSideWindow(
            VegetationMeshBuffer buffer,
            in PlantPlacement place,
            int wallSubmesh,
            float wallX,
            float baseY,
            float centerZ,
            float halfWidth,
            float height,
            bool facingRight,
            float litChance,
            ref PlantRandom random)
        {
            float sign = facingRight ? 1f : -1f;
            float y1 = baseY + height;
            float back = wallX - sign * Reveal;

            // The recess is cut along -X on one flank and along +X on the other, and a reflection reverses
            // winding — so a single hard-coded corner order can only ever be right for one of them. It was:
            // every window on the right-hand flank was inside out, which reads as a window that is simply
            // missing rather than as an error. Swapping the two z corners with the flank mirrors it back,
            // and the whole helper is then correct on both sides for the same reason it is correct on one.
            float zNear = centerZ + halfWidth * sign;
            float zFar = centerZ - halfWidth * sign;

            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(wallX, baseY, zNear), place.ToWorld(back, baseY, zNear),
                place.ToWorld(back, y1, zNear), place.ToWorld(wallX, y1, zNear), -place.Forward * sign);
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(back, baseY, zFar), place.ToWorld(wallX, baseY, zFar),
                place.ToWorld(wallX, y1, zFar), place.ToWorld(back, y1, zFar), place.Forward * sign);
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(wallX, y1, zNear), place.ToWorld(back, y1, zNear),
                place.ToWorld(back, y1, zFar), place.ToWorld(wallX, y1, zFar), -place.Up);
            buffer.AddQuadFacing(wallSubmesh,
                place.ToWorld(back, baseY, zNear), place.ToWorld(wallX, baseY, zNear),
                place.ToWorld(wallX, baseY, zFar), place.ToWorld(back, baseY, zFar), place.Up);

            buffer.AddQuadFacing(GlassSubmesh(ref random, litChance),
                place.ToWorld(back, baseY, zNear), place.ToWorld(back, baseY, zFar),
                place.ToWorld(back, y1, zFar), place.ToWorld(back, y1, zNear), place.Right * sign);

            AddBox(buffer, place, TrimSubmesh, wallX + sign * 0.04f, baseY - 0.1f, centerZ,
                0.09f, 0.09f, halfWidth + 0.1f);
        }

    }
}
