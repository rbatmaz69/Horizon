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
        /// Paint on the forecourt: the bay outlines that say where to stop.
        ///
        /// <para>Geometry rather than a texture, which is what <c>TownStreetBuilder.MarkingSubmesh</c>
        /// settled for every marking in the world that is not on the trunk road. Laid-on quads cost two
        /// triangles a stripe and merge into the same tinted material as everything else here, so they
        /// are free at the draw call.</para>
        /// </summary>
        public const int MarkingSubmesh = 3;

        /// <summary>
        /// The shop's glazing and the pumps' displays — the things that are dark glass by day and lit
        /// after dusk.
        ///
        /// <para>Left untinted so it survives <c>MergeTinted</c> on a submesh of its own, exactly as
        /// <c>HarbourMeshes.LanternSubmesh</c> does: a colour baked into a vertex cannot be swapped, and
        /// swapping is the whole point of a lit slot.</para>
        ///
        /// <para><b>Registered under <c>LitGroup.Windows</c>, and not under <c>Lamps</c>.</b> It was
        /// Lamps at first, which was a real bug rather than a preference: that group's day material is
        /// <c>M_Lane</c>, the road's own asphalt, because a lamp's pool of light is meant to vanish into
        /// the carriageway by day. Applied to a shop window it paints it tarmac. Windows gives dark
        /// glass by day — right for glazing, right for an unlit pump display, and right for a luminaire
        /// that is switched off.</para>
        /// </summary>
        public const int LitSubmesh = 4;

        /// <summary>
        /// The sign faces, and the canopy's light strips — everything that is bright in both states.
        ///
        /// <para><b>Its own slot because no <c>LitGroup</c> can express what a sign does.</b> Every
        /// group swaps between a day material and a night one, and an illuminated sign is the one thing
        /// on a forecourt that looks the same at noon as at midnight. Registered with nothing, and given
        /// a plain bright unlit material that never changes.</para>
        ///
        /// <para>This is most of why the stations were hard to find. The sign face used to share the lit
        /// slot, so in daylight the object whose entire job is to advertise the place was painted with
        /// road asphalt.</para>
        /// </summary>
        public const int SignSubmesh = 5;

        public const int SubmeshCount = 6;

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

        /// <summary>
        /// Road paint. The motorway's value, restated rather than referenced.
        ///
        /// <para><c>RoadTextureBuilder.PaintBase</c> is the original and is Editor-only, so it cannot
        /// compile into a player — the same bind <c>MotorwayMergeBuilder.SurfaceTints</c> is in, and it
        /// restates this exact literal for the same reason. Take the motorway's and not the town
        /// street's paler 0.82: using the wrong one of the two once laid a visibly paler strip down the
        /// side of the motorway.</para>
        /// </summary>
        private static readonly Color MarkingColour = new Color(0.86f, 0.86f, 0.83f);

        /// <summary>One entry per submesh, null where it must keep its own material.</summary>
        public static Color?[] Tints()
        {
            var tints = new Color?[SubmeshCount];
            tints[ApronSubmesh] = ApronColour;
            tints[StructureSubmesh] = StructureColour;
            tints[TrimSubmesh] = TrimColour;
            tints[MarkingSubmesh] = MarkingColour;

            // LitSubmesh and SignSubmesh stay null. See the constants.
            return tints;
        }

        /// <summary>
        /// A sign standing beside the road some way before the station it announces.
        ///
        /// <para>A second world frame on the same site, which is the one place this file's rule about
        /// never learning where it is has to bend: the road curves over the 250 m between the sign and
        /// the forecourt, so one frame cannot describe both ends. It comes from the same path and the
        /// same side as the station, which is what keeps the two honest.</para>
        /// </summary>
        public readonly struct AdvanceSign
        {
            /// <summary>False where no spot up the road was clear enough to stand one.</summary>
            public readonly bool Exists;

            /// <summary>Ground at the base of the post.</summary>
            public readonly Vector3 Foot;

            public readonly Vector3 Forward;

            /// <summary>Across the road towards the sign, side folded in.</summary>
            public readonly Vector3 Outward;

            /// <summary>How far upstream of the station it ended up, metres. For the build log.</summary>
            public readonly float Distance;

            public AdvanceSign(Vector3 foot, Vector3 forward, Vector3 outward, float distance)
            {
                Exists = true;
                Foot = foot;
                Forward = forward;
                Outward = outward;
                Distance = distance;
            }
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

            /// <summary>Its advance sign, if a clear spot was found for one.</summary>
            public readonly AdvanceSign Sign;

            public StationSite(
                Vector3 centre, Vector3 forward, Vector3 outward, float roadEdge, string name, uint seed,
                AdvanceSign sign = default)
            {
                Centre = centre;
                Forward = forward;
                Outward = outward;
                RoadEdge = roadEdge;
                Name = name;
                Seed = seed;
                Sign = sign;
            }

            /// <summary>
            /// The same station with its sign dropped.
            ///
            /// <para>For the one hazard the course cannot see: <c>Sites</c> resolves signs before the
            /// height field exists, so whether a spot is under water is a question only the caller can
            /// answer, and only later.</para>
            /// </summary>
            public StationSite WithoutSign()
            {
                return new StationSite(Centre, Forward, Outward, RoadEdge, Name, Seed);
            }
        }

        /// <summary>How far the sign's post stands from the carriageway's paved edge, metres.</summary>
        private const float SignStandoff = 3.5f;

        private const float SignBoardHalf = 1.5f;
        private const float SignBoardDeep = 0.16f;
        private const float SignBoardTall = 1.8f;

        /// <summary>Bottom edge of the board above the ground, so its top reaches 4 m.</summary>
        private const float SignBoardFoot = 2.2f;

        private const float SignPostHalf = 0.18f;

        /// <summary>
        /// How far below the road line the post is sunk, metres.
        ///
        /// <para>The ground is never queried for this. Within <c>TerrainShape.VergeWidth</c> — 24 m —
        /// of any road sample the field returns a dead-flat shelf at the carriageway's height less
        /// <c>RoadShelfDrop</c>, and a sign 10 m out is well inside that, so its foot is at most a
        /// couple of decimetres out from a straight guess. Burying most of a metre of post swallows
        /// that, and <see cref="AddBox"/> emits no bottom face to give it away.</para>
        /// </summary>
        private const float SignBury = 0.8f;

        /// <summary>
        /// The sign that stands up the road from a station, announcing it.
        ///
        /// <para><b>This is the object that answers "where are the filling stations".</b> The totem at
        /// the entrance only helps somebody already level with the place; by then the decision to pull
        /// in has been made or missed. This one is 250 m back, which is nine seconds at 100 km/h.</para>
        ///
        /// <para>Facing oncoming traffic, which is the whole difference between it and the totem, and
        /// the frame is handed to <see cref="AddBox"/> swapped to get it: the board is 32 cm along the
        /// road and 3 m across it, so its broad faces point up and down the carriageway.</para>
        /// </summary>
        public static void AddAdvanceSign(VegetationMeshBuffer buffer, in AdvanceSign sign)
        {
            if (!sign.Exists)
            {
                return;
            }

            Vector3 foot = sign.Foot - Vector3.up * SignBury;

            AddBox(buffer, StructureSubmesh, foot, sign.Forward, sign.Outward,
                SignPostHalf, SignPostHalf, SignBoardFoot + SignBury);

            Vector3 board = sign.Foot + Vector3.up * SignBoardFoot;

            AddBox(buffer, SignSubmesh, board, sign.Forward, sign.Outward,
                SignBoardDeep, SignBoardHalf, SignBoardTall);

            AddPictogram(buffer, board + Vector3.up * (SignBoardTall * 0.5f),
                sign.Forward, sign.Outward, SignBoardDeep, 0.62f);
        }

        /// <summary>How far out from the paved edge a sign's post stands. Read by the resolver.</summary>
        public static float AdvanceSignStandoff => SignStandoff;

        /// <summary>Lays one station into <paramref name="buffer"/>.</summary>
        public static void AddStation(VegetationMeshBuffer buffer, in StationSite site)
        {
            var random = new PlantRandom(site.Seed);

            AddApron(buffer, site);
            AddCanopy(buffer, site);

            // AddPumps hands back its own dice roll rather than AddBays asking for it again. One
            // PlantRandom drawn in order is what makes a station reproducible, and the price of that is
            // that anything downstream of a roll has to be told the answer: a second PlantRandom(Seed)
            // would draw in a different order and paint an aisle beside an island that is not there.
            bool thirdIsland = AddPumps(buffer, site, ref random);

            AddShop(buffer, site, ref random);
            AddBays(buffer, site, thirdIsland);
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

            // The underside, which AddBox does not give: it emits a top and four sides and no bottom,
            // deliberately, because nothing else in this file is ever seen from below. A canopy is —
            // by a driver who has stopped directly under it, which is the whole point of the object.
            // Without this quad the roof over the pumps is a hole onto the sky with three light strips
            // hanging in it, and at night that reads as a dark soffit rather than as the bug it is.
            Vector3 alongDeck = site.Forward * CanopyHalfLength;
            Vector3 acrossDeck = site.Outward * CanopyHalfDepth;

            buffer.AddQuadFacing(StructureSubmesh,
                deck - alongDeck - acrossDeck, deck - alongDeck + acrossDeck,
                deck + alongDeck + acrossDeck, deck + alongDeck - acrossDeck,
                Vector3.down);

            // The fascia hangs below the deck's edge on all four sides. It is the band of colour that
            // says what this place is from further away than any of the detail under it.
            AddRim(buffer, TrimSubmesh, deck, site.Forward, site.Outward,
                CanopyHalfLength, CanopyHalfDepth, -FasciaDepth);

            // Light in the soffit — three strips, not the whole ceiling.
            //
            // The whole ceiling was tried first, on the reasoning that a forecourt canopy reads as a lit
            // plane at night. It does in life, because the light falls out of it onto everything below.
            // Here it cannot: the lit slot is an unlit material, so a 22-by-11-metre panel of it is 240
            // square metres of pure white filling half the screen the moment the car pulls under, with
            // the pumps in silhouette against it. It looked like a hole in the world rather than like a
            // roof, and no amount of tinting fixes a surface that emits and does not illuminate.
            //
            // Three narrow luminaires are what a real canopy actually has, and they work here for the
            // same reason the town's lamp heads do: small bright things read as lights. About a
            // twentieth of the area, and the soffit around them is the quad above.
            //
            // On the sign slot rather than the lit one, and it took two wrong answers to get here.
            // LitGroup.Lamps is what a lamp head uses, and its day material is M_Lane — the road's own
            // asphalt — because a pool of light has to vanish into the carriageway when it is off. It
            // only vanishes when the surface it is painted on *is* the road; on a pale soffit it is a
            // grey stripe. LitGroup.Windows is not right either: a diffuser that is switched off is
            // white, not near-black glass. What a canopy luminaire actually is, is the same object as
            // the sign — white by day, bright by night, one material for both — so it shares its slot
            // and costs nothing.
            Vector3 soffit = deck - Vector3.up * 0.02f;
            Vector3 a = site.Forward * (CanopyHalfLength - 1.2f);

            for (int i = -1; i <= 1; i++)
            {
                Vector3 across = site.Outward * (i * CanopyHalfDepth * 0.55f);
                Vector3 w = site.Outward * 0.32f;

                buffer.AddQuadFacing(SignSubmesh,
                    soffit - a + across - w, soffit + a + across - w,
                    soffit + a + across + w, soffit - a + across + w,
                    Vector3.down);
            }

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
        /// <returns>Whether the third island was rolled, which the bay paint has to know.</returns>
        private static bool AddPumps(
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
            if (!random.Chance(0.5f))
            {
                return false;
            }

            Vector3 third = centre + site.Outward * 7.4f;
            AddBox(buffer, TrimSubmesh, third, site.Forward, site.Outward, 5f, 1.1f, 0.18f);
            return true;
        }

        /// <summary>
        /// The painted aisles, which are what say where to stop.
        ///
        /// <para><b>Per aisle, not per pump.</b> An aisle is exactly one car wide and runs parallel to
        /// the road, so it is the space a car is actually in — and from it a driver can reach a pump on
        /// each hand. Marking the pumps instead would mark two things a car cannot occupy.</para>
        ///
        /// <para>The positions are read off where <see cref="AddPumps"/> put the islands rather than
        /// written down again: the canopy centre is 1.5 m out, the islands sit ±2.6 either side of it
        /// with a 1.1 m half-depth, so in metres from the site centre they occupy −2.2…0.0 and 3.0…5.2,
        /// and the third, when it is rolled, 7.8…10.0. What is left between them is the aisles.</para>
        ///
        /// <para>Each gets two lines down its edges and a bar across the middle. The lines run three
        /// metres past the islands at both ends, which is a lead-in funnel where a car enters — and the
        /// bar divides fore from aft, putting one bay beside each pump. Six triangles an aisle.</para>
        /// </summary>
        private static void AddBays(VegetationMeshBuffer buffer, in StationSite site, bool thirdIsland)
        {
            AddBay(buffer, site, -5.0f, -2.2f);
            AddBay(buffer, site, 0f, 3.0f);

            if (thirdIsland)
            {
                AddBay(buffer, site, 5.2f, 7.8f);
            }
        }

        /// <summary>One aisle's paint, between two island edges given in metres of Outward.</summary>
        private static void AddBay(
            VegetationMeshBuffer buffer, in StationSite site, float low, float high)
        {
            // 24 cm, wider than the 10-to-15 a real bay is painted at. This world is read at speed and
            // from a car, and the same argument the town's markings make about a 14 cm kerb applies:
            // the geometry that is technically correct is below the width at which anything registers.
            const float lineHalf = 0.12f;
            const float inset = 0.10f;
            const float reach = 8f;

            float left = low + inset + lineHalf;
            float right = high - inset - lineHalf;

            AddStripe(buffer, site, left - lineHalf, left + lineHalf, -reach, reach);
            AddStripe(buffer, site, right - lineHalf, right + lineHalf, -reach, reach);
            AddStripe(buffer, site, left, right, -lineHalf, lineHalf);
        }

        /// <summary>
        /// One painted rectangle, laid on the slab.
        ///
        /// <para><b>A constant lift is legitimate here and would not be on a road.</b> The technique is
        /// <c>TownStreetBuilder</c>'s, and so is the trap: paint lifted a flat 1.5 cm over a carriageway
        /// with a 6 cm crown sat <i>underneath</i> the asphalt on every marked street in the town, which
        /// is what commit 08aba1f had to unpick with a height that follows the camber. This slab has no
        /// camber — <see cref="AddApron"/> emits it as a single flat quad and
        /// <c>FuelStationBuilder.AddPadSamples</c> levels the ground under it flat across and level
        /// along — so there is nothing to follow, and that is why the machinery is absent rather than
        /// forgotten.</para>
        ///
        /// <para>Two centimetres, the motorway merge's figure and its argument: it beats the depth
        /// buffer outright and is a twentieth of the suspension's travel at rest, so a raycast wheel
        /// crossing a stripe cannot feel the step.</para>
        ///
        /// <para>Nothing here comes near the entry ramp, and by construction rather than by care: the
        /// paint stays inside ±8 m along and −5…8 across, while the ramp starts at −19.5 across. Nine
        /// metres of flat slab separate them, and it would take <see cref="ApronHalfDepth"/> shrinking
        /// below about 9 to close that.</para>
        /// </summary>
        private static void AddStripe(
            VegetationMeshBuffer buffer,
            in StationSite site,
            float acrossFrom,
            float acrossTo,
            float alongFrom,
            float alongTo)
        {
            const float lift = 0.02f;

            Vector3 top = site.Centre + Vector3.up * lift;

            Vector3 a = top + site.Forward * alongFrom + site.Outward * acrossFrom;
            Vector3 b = top + site.Forward * alongTo + site.Outward * acrossFrom;
            Vector3 c = top + site.Forward * alongTo + site.Outward * acrossTo;
            Vector3 d = top + site.Forward * alongFrom + site.Outward * acrossTo;

            buffer.AddQuadFacing(MarkingSubmesh, a, b, c, d, Vector3.up);
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

            Vector3 panel = foot + Vector3.up * (TotemHeight - 1.8f);

            // Broadside to the road — and it was not, which is half of why nobody could find these.
            //
            // AddBox's halfLength runs along `forward`, so the first version's 1.5 by 0.16 made a slab
            // 3 m long down the road and 32 cm across it: broad faces pointing at the verge, and a
            // sixteen-centimetre edge presented to the only person who was ever going to read it. The
            // comment said broadside and the geometry did the opposite. Swapped, it is 32 cm along the
            // road and 3 m across, which is a sign.
            AddBox(buffer, SignSubmesh, panel, site.Forward, site.Outward, 0.16f, 1.5f, 1.8f);

            AddPictogram(buffer, panel + Vector3.up * 0.9f, site.Forward, site.Outward, 0.16f, 0.62f);
        }

        /// <summary>
        /// The pump symbol, standing proud of a sign face.
        ///
        /// <para><b>A bright blank rectangle says "something is coming"; it does not say "fuel".</b>
        /// This world has bright rectangles in it already — lit windows, the totem, the shop front — so
        /// a face with nothing on it is one more of those. There is no text to draw with: nothing in
        /// this project renders a glyph in world space, and an atlas for four signs would be an art
        /// pipeline for four signs.</para>
        ///
        /// <para>So it is three boxes in the dark trim colour, in the proportions the HUD's own pump
        /// glyph settled on — see <c>HorizonAssetUtility.GlyphAlpha</c>'s "fuel" case, whose numbers
        /// were arrived at by drawing it and looking at it at forty units and again at twenty. A body,
        /// the hose arm beside it, and a plinth. Standing 4 cm off the face, which is the same trick
        /// <c>TrafficSignalMeshes.LensProud</c> uses at 1.2 cm and for the same reason: two coplanar
        /// surfaces are two surfaces fighting for the depth buffer.</para>
        /// </summary>
        /// <param name="faceHalfDepth">
        /// Half the thickness of the panel this sits on, metres. It has to be passed in and not guessed:
        /// the symbol goes <i>proud of the face</i>, and a version that measured from the panel's centre
        /// instead put every bar inside the sign, where they were rendered exactly as asked and seen by
        /// nobody.
        /// </param>
        /// <param name="scale">Half-height of the symbol, metres. It is drawn to a unit square.</param>
        private static void AddPictogram(
            VegetationMeshBuffer buffer,
            Vector3 centre,
            Vector3 forward,
            Vector3 outward,
            float faceHalfDepth,
            float scale)
        {
            const float proud = 0.05f;
            float standOff = faceHalfDepth + proud * 0.5f;

            // Both faces of the sign carry it, so it reads whichever way the sign is turned.
            for (int face = -1; face <= 1; face += 2)
            {
                Vector3 at = centre + forward * (standOff * face);

                // Body, hose arm, plinth — laid out across `outward`, because that is the sign's width.
                AddSymbolBar(buffer, at, forward, outward, proud, -0.28f * scale, 0.02f, 0.30f * scale, 0.58f * scale);
                AddSymbolBar(buffer, at, forward, outward, proud, 0.40f * scale, 0.02f, 0.08f * scale, 0.44f * scale);
                AddSymbolBar(buffer, at, forward, outward, proud, -0.28f * scale, -0.62f * scale, 0.40f * scale, 0.10f * scale);
            }
        }

        /// <summary>One bar of the pump symbol: a flat box standing off the sign's face.</summary>
        private static void AddSymbolBar(
            VegetationMeshBuffer buffer,
            Vector3 faceCentre,
            Vector3 forward,
            Vector3 outward,
            float thickness,
            float across,
            float up,
            float halfAcross,
            float halfUp)
        {
            Vector3 at = faceCentre + outward * across + Vector3.up * (up - halfUp);

            AddBox(buffer, TrimSubmesh, at, forward, outward, thickness * 0.5f, halfAcross, halfUp * 2f);
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
