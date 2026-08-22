using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// One filling station, authored in its own frame and emitted into a shared buffer.
    ///
    /// <para><b>Everything here is written in metres along, across and up from the forecourt's centre</b>,
    /// and never in world coordinates — the same discipline <c>HarbourMeshes</c> keeps. The station has to
    /// come out the same whether it faces north on the motorway or south-east above Talheim, and the only
    /// way to be sure of that is for the shapes never to learn where they are.</para>
    ///
    /// <para><b>What a station is for, in order of importance.</b> The totem sign first: it is how a driver
    /// learns there is fuel ahead while there is still time to lift, and at four hundred metres it is the
    /// only part of this that is more than a smudge. Then the canopy, which is the silhouette that says
    /// what the place is. The pumps and the shop are detail for the last fifty metres. Sized in that
    /// order too — the sign is deliberately taller than a station of this size would really carry.</para>
    ///
    /// <para>Three tinted submeshes and one left bare. The tinted ones merge into the single vertex-tint
    /// material the buildings already use; the bare one is the glazing and the sign face, which
    /// <c>TownLights</c> swaps to a lit material after dusk and therefore cannot have a colour baked into
    /// its vertices. Two draw calls for the whole station.</para>
    /// </summary>
    public static class FuelStationMeshes
    {
        /// <summary>The concrete slab, its skirt and the entry ramp.</summary>
        public const int ApronSubmesh = 0;

        /// <summary>Canopy deck, columns, shop walls, pump bodies, the sign's post.</summary>
        public const int StructureSubmesh = 1;

        /// <summary>The canopy fascia and the pump islands — the paint that reads from the road.</summary>
        public const int TrimSubmesh = 2;

        /// <summary>
        /// The sign's face, the shop's glazing and the pumps' displays.
        ///
        /// <para>Left untinted so it survives <c>MergeTinted</c> on a submesh of its own, exactly as
        /// <c>HarbourMeshes.LanternSubmesh</c> does: a colour baked into a vertex cannot be swapped, and
        /// swapping is the whole point of a lit slot.</para>
        /// </summary>
        public const int LitSubmesh = 3;

        public const int SubmeshCount = 4;

        /// <summary>Half the forecourt along the road, metres.</summary>
        public const float ApronHalfLength = 26f;

        /// <summary>Half the forecourt across it, measured from the centre of the slab.</summary>
        public const float ApronHalfDepth = 17f;

        /// <summary>How far the sign's panel reaches above the forecourt, metres.</summary>
        public const float TotemHeight = 8.5f;

        /// <summary>Clear height under the canopy. A van is 2.6 m, and a real canopy is signed at 4.</summary>
        private const float CanopyClear = 4.6f;

        private const float CanopyHalfLength = 11f;
        private const float CanopyHalfDepth = 5.5f;
        private const float CanopyDeck = 0.32f;
        private const float FasciaDepth = 0.55f;
        private const float ColumnHalf = 0.18f;

        /// <summary>How far the slab drops below its top face before the ground is left to it.</summary>
        private const float SkirtDrop = 0.32f;

        private static readonly Color ApronColour = new Color(0.44f, 0.44f, 0.43f);
        private static readonly Color StructureColour = new Color(0.87f, 0.86f, 0.82f);
        private static readonly Color TrimColour = new Color(0.84f, 0.38f, 0.20f);

        /// <summary>One entry per submesh, null where it must keep its own material.</summary>
        public static Color?[] Tints()
        {
            var tints = new Color?[SubmeshCount];
            tints[ApronSubmesh] = ApronColour;
            tints[StructureSubmesh] = StructureColour;
            tints[TrimSubmesh] = TrimColour;

            // LitSubmesh stays null. See the constant.
            return tints;
        }

        /// <summary>Where one station stands and which way it faces.</summary>
        public readonly struct StationSite
        {
            /// <summary>Centre of the forecourt slab, at the height of its top face.</summary>
            public readonly Vector3 Centre;

            /// <summary>Unit vector along the road, in the direction of travel.</summary>
            public readonly Vector3 Forward;

            /// <summary>
            /// Unit vector from the carriageway towards the forecourt — the side folded in already, so
            /// nothing downstream has to remember which way a left-hand station faces.
            /// </summary>
            public readonly Vector3 Outward;

            /// <summary>How far the carriageway's edge is from <see cref="Centre"/>, metres.</summary>
            public readonly float RoadEdge;

            public readonly string Name;
            public readonly uint Seed;

            public StationSite(
                Vector3 centre, Vector3 forward, Vector3 outward, float roadEdge, string name, uint seed)
            {
                Centre = centre;
                Forward = forward;
                Outward = outward;
                RoadEdge = roadEdge;
                Name = name;
                Seed = seed;
            }
        }

        /// <summary>Lays one station into <paramref name="buffer"/>.</summary>
        public static void AddStation(VegetationMeshBuffer buffer, in StationSite site)
        {
            var random = new PlantRandom(site.Seed);

            AddApron(buffer, site);
            AddCanopy(buffer, site);
            AddPumps(buffer, site, ref random);
            AddShop(buffer, site, ref random);
            AddTotem(buffer, site);
        }

        /// <summary>
        /// The slab, its skirt and the ramp up off the carriageway.
        ///
        /// <para>The skirt is not decoration. A slab with no thickness is a single plane sitting a few
        /// centimetres over the ground, and the raycast wheels find whichever of the two the ray happens
        /// to hit first — so a car crossing the edge drops through it. Boxing the edge in gives the
        /// suspension something continuous to run up.</para>
        /// </summary>
        private static void AddApron(VegetationMeshBuffer buffer, in StationSite site)
        {
            Vector3 along = site.Forward * ApronHalfLength;
            Vector3 across = site.Outward * ApronHalfDepth;
            Vector3 top = site.Centre;

            Vector3 a = top - along - across;
            Vector3 b = top + along - across;
            Vector3 c = top + along + across;
            Vector3 d = top - along + across;

            buffer.AddQuadFacing(ApronSubmesh, a, b, c, d, Vector3.up);

            Vector3 drop = Vector3.down * SkirtDrop;
            AddSkirt(buffer, a, b, drop, -site.Outward);
            AddSkirt(buffer, b, c, drop, site.Forward);
            AddSkirt(buffer, c, d, drop, site.Outward);
            AddSkirt(buffer, d, a, drop, -site.Forward);

            // The way in. A short wedge from the shoulder up to the slab across the whole frontage,
            // rather than two dropped kerbs: a continuous open frontage is what a rural forecourt has,
            // and two throats would be two more places for a wheel to catch on nothing.
            Vector3 lip = site.Centre - site.Outward * (ApronHalfDepth + 2.5f) + Vector3.down * SkirtDrop;
            Vector3 e = lip - along;
            Vector3 f = lip + along;

            buffer.AddQuadFacing(ApronSubmesh, e, f, b, a, Vector3.up);
        }

        private static void AddSkirt(
            VegetationMeshBuffer buffer, Vector3 from, Vector3 to, Vector3 drop, Vector3 outward)
        {
            buffer.AddQuadFacing(ApronSubmesh, from, to, to + drop, from + drop, outward);
        }

        /// <summary>The canopy: a flat deck on four columns, with the fascia that carries the colour.</summary>
        private static void AddCanopy(VegetationMeshBuffer buffer, in StationSite site)
        {
            // Set back from the road so the pumps under it are not on the frontage, and short of the
            // shop behind.
            Vector3 centre = site.Centre + site.Outward * 1.5f;
            Vector3 deck = centre + Vector3.up * CanopyClear;

            AddBox(buffer, StructureSubmesh, deck, site.Forward, site.Outward,
                CanopyHalfLength, CanopyHalfDepth, CanopyDeck);

            // The fascia hangs below the deck's edge on all four sides. It is the band of colour that
            // says what this place is from further away than any of the detail under it.
            AddRim(buffer, TrimSubmesh, deck, site.Forward, site.Outward,
                CanopyHalfLength, CanopyHalfDepth, -FasciaDepth);

            for (int i = 0; i < 4; i++)
            {
                float alongSign = (i & 1) == 0 ? -1f : 1f;
                float acrossSign = (i & 2) == 0 ? -1f : 1f;

                Vector3 foot = centre
                               + site.Forward * (alongSign * (CanopyHalfLength - 1.6f))
                               + site.Outward * (acrossSign * (CanopyHalfDepth - 1.2f));

                AddBox(buffer, StructureSubmesh, foot, site.Forward, site.Outward,
                    ColumnHalf, ColumnHalf, CanopyClear);
            }
        }

        /// <summary>Two islands under the canopy, two pumps on each.</summary>
        private static void AddPumps(
            VegetationMeshBuffer buffer, in StationSite site, ref PlantRandom random)
        {
            Vector3 centre = site.Centre + site.Outward * 1.5f;

            for (int island = 0; island < 2; island++)
            {
                float acrossSign = island == 0 ? -1f : 1f;
                Vector3 at = centre + site.Outward * (acrossSign * 2.6f);

                AddBox(buffer, TrimSubmesh, at, site.Forward, site.Outward, 5f, 1.1f, 0.18f);

                for (int pump = 0; pump < 2; pump++)
                {
                    float alongSign = pump == 0 ? -1f : 1f;
                    Vector3 foot = at + site.Forward * (alongSign * 2.4f) + Vector3.up * 0.18f;

                    AddBox(buffer, StructureSubmesh, foot, site.Forward, site.Outward, 0.55f, 0.34f, 0.9f);

                    Vector3 head = foot + Vector3.up * 0.9f;
                    AddBox(buffer, StructureSubmesh, head, site.Forward, site.Outward, 0.55f, 0.34f, 0.75f);

                    // The display, on both long faces. Small, and lit — at night a rank of these under
                    // the canopy is most of what makes a forecourt read as open rather than derelict.
                    AddPanel(buffer, LitSubmesh, head + Vector3.up * 0.42f,
                        site.Forward, site.Outward, 0.36f, 0.35f, 0.20f);
                }
            }

            // A third island where there is room for it, so seven stations are not one station seven
            // times. The Talheim and Passhöhe pads are the same size as the motorway's, and a service
            // area on a trunk road ought to look busier than one on a mountain.
            if (random.Chance(0.5f))
            {
                Vector3 at = centre + site.Outward * 7.4f;
                AddBox(buffer, TrimSubmesh, at, site.Forward, site.Outward, 5f, 1.1f, 0.18f);
            }
        }

        /// <summary>The shop, at the back of the forecourt with its glazing towards the pumps.</summary>
        private static void AddShop(
            VegetationMeshBuffer buffer, in StationSite site, ref PlantRandom random)
        {
            float halfLength = random.Range(4.2f, 5.4f);
            const float halfDepth = 3.2f;
            const float height = 3.4f;

            Vector3 at = site.Centre + site.Outward * (ApronHalfDepth - halfDepth - 1.6f);

            AddBox(buffer, StructureSubmesh, at, site.Forward, site.Outward, halfLength, halfDepth, height);

            // A shallow parapet, which is what stops a flat-roofed box reading as a crate.
            AddRim(buffer, TrimSubmesh, at + Vector3.up * height, site.Forward, site.Outward,
                halfLength, halfDepth, 0.4f);

            // Glazing across the front, proud of the wall so it does not z-fight with it.
            AddPanel(buffer, LitSubmesh,
                at - site.Outward * (halfDepth + 0.04f) + Vector3.up * 1.9f,
                site.Forward, site.Outward, halfLength - 0.6f, 0f, 1.2f);
        }

        /// <summary>
        /// The sign by the road.
        ///
        /// <para>The most important object on the forecourt and the only one sized for distance rather
        /// than for realism. A station the driver sees only once they are level with it is a station they
        /// have already passed, so the panel goes up where it clears the canopy and everything the
        /// vegetation keep-out has left standing behind it.</para>
        /// </summary>
        private static void AddTotem(VegetationMeshBuffer buffer, in StationSite site)
        {
            Vector3 foot = site.Centre
                           - site.Outward * (ApronHalfDepth - 2f)
                           + site.Forward * (ApronHalfLength - 4f);

            AddBox(buffer, StructureSubmesh, foot, site.Forward, site.Outward,
                0.32f, 0.32f, TotemHeight - 1.8f);

            Vector3 panel = foot + Vector3.up * (TotemHeight - 0.9f);

            // Broadside to the road, so it is read by someone driving towards it rather than by someone
            // standing on the forecourt looking back.
            AddBox(buffer, LitSubmesh, panel - Vector3.up * 0.9f,
                site.Forward, site.Outward, 1.5f, 0.16f, 1.8f);
        }

        /// <summary>A box standing on <paramref name="foot"/> and rising <paramref name="height"/>.</summary>
        private static void AddBox(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 foot,
            Vector3 forward,
            Vector3 outward,
            float halfLength,
            float halfDepth,
            float height)
        {
            Vector3 a = forward * halfLength;
            Vector3 d = outward * halfDepth;
            Vector3 up = Vector3.up * height;

            Vector3 b0 = foot - a - d;
            Vector3 b1 = foot + a - d;
            Vector3 b2 = foot + a + d;
            Vector3 b3 = foot - a + d;

            buffer.AddQuadFacing(submesh, b0 + up, b1 + up, b2 + up, b3 + up, Vector3.up);
            buffer.AddQuadFacing(submesh, b0, b1, b1 + up, b0 + up, -outward);
            buffer.AddQuadFacing(submesh, b2, b3, b3 + up, b2 + up, outward);
            buffer.AddQuadFacing(submesh, b1, b2, b2 + up, b1 + up, forward);
            buffer.AddQuadFacing(submesh, b3, b0, b0 + up, b3 + up, -forward);
        }

        /// <summary>
        /// A band round the four edges of a rectangle, hanging below it or standing above it.
        ///
        /// <para>Negative <paramref name="height"/> hangs — that is the canopy fascia; positive stands,
        /// which is the shop's parapet. One helper because they are the same four quads.</para>
        /// </summary>
        private static void AddRim(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 top,
            Vector3 forward,
            Vector3 outward,
            float halfLength,
            float halfDepth,
            float height)
        {
            Vector3 a = forward * halfLength;
            Vector3 d = outward * halfDepth;
            Vector3 rise = Vector3.up * height;

            Vector3 c0 = top - a - d;
            Vector3 c1 = top + a - d;
            Vector3 c2 = top + a + d;
            Vector3 c3 = top - a + d;

            buffer.AddQuadFacing(submesh, c0, c1, c1 + rise, c0 + rise, -outward);
            buffer.AddQuadFacing(submesh, c2, c3, c3 + rise, c2 + rise, outward);
            buffer.AddQuadFacing(submesh, c1, c2, c2 + rise, c1 + rise, forward);
            buffer.AddQuadFacing(submesh, c3, c0, c0 + rise, c3 + rise, -forward);
        }

        /// <summary>
        /// A flat panel facing across the road, double-sided.
        ///
        /// <para>Double-sided because these are glazing and displays, which are seen from both hands, and
        /// a single quad seen from behind is a hole. Cheaper than boxing them at this size.</para>
        /// </summary>
        private static void AddPanel(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 centre,
            Vector3 forward,
            Vector3 outward,
            float halfLength,
            float offset,
            float halfHeight)
        {
            Vector3 at = centre - outward * offset;
            Vector3 a = forward * halfLength;
            Vector3 up = Vector3.up * halfHeight;

            buffer.AddDoubleSided(submesh, at - a - up, at + a - up, at + a + up);
            buffer.AddDoubleSided(submesh, at - a - up, at + a + up, at - a + up);
        }
    }
}
