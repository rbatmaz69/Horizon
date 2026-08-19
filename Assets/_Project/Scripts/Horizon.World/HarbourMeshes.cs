using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Everything Seeburg's harbour is made of: quay walls, two moles, a lighthouse, pontoons, moored
    /// boats and the promenade rail along the front.
    ///
    /// <para><b>Its own file rather than an addition to <see cref="LandmarkMeshes"/>.</b> That one builds
    /// single set-piece buildings that a town's plot pass places like any other plot. None of this is a
    /// plot: a quay wall is three hundred metres of retaining structure that follows the edge of a
    /// dredged basin, and a mole is a linear embankment out into open water. They are laid against
    /// geometry — an arc, a shoreline, a street — the way guard rails and bridges are, not against a
    /// frontage.</para>
    ///
    /// <para><b>Five submeshes, four of them tinted.</b> The tinted four merge into one on the way out
    /// (<c>VegetationMeshBuffer.MergeTinted</c>), so the whole harbour is a single draw call on the
    /// vertex-tint material every building in the world already uses. The fifth is the lighthouse
    /// lantern, which has to keep a material of its own because <see cref="TownLights"/> swaps it after
    /// dusk — a colour merged into a mesh is merged for good.</para>
    ///
    /// <para><b>Angles are measured from the basin's centre, zero facing land.</b> That is the one frame
    /// in which every part of a harbour is easy to say: the quay is the arc either side of zero, the
    /// moles are the arcs that sweep out from the ends of it, and the mouth is the gap they leave
    /// opposite. Nothing here is authored in world coordinates.</para>
    /// </summary>
    public static class HarbourMeshes
    {
        /// <summary>Quay walls, mole armour and the lighthouse shaft.</summary>
        public const int StoneSubmesh = 0;

        /// <summary>Copings, bollards, piles, railings and the lighthouse's bands.</summary>
        public const int CopingSubmesh = 1;

        /// <summary>Pontoon decking, benches and boat decks.</summary>
        public const int DeckSubmesh = 2;

        /// <summary>Painted boat hulls.</summary>
        public const int HullSubmesh = 3;

        /// <summary>
        /// The lantern. Left untinted so it survives <c>MergeTinted</c> on a submesh of its own — see the
        /// class remarks.
        /// </summary>
        public const int LanternSubmesh = 4;

        public const int SubmeshCount = 5;

        /// <summary>Height of the lantern above the mole head, metres — the tallest thing in Seeburg.</summary>
        public const float LighthouseHeight = 21f;

        private static readonly Color StoneColour = new Color(0.55f, 0.54f, 0.50f);
        private static readonly Color CopingColour = new Color(0.72f, 0.70f, 0.65f);
        private static readonly Color DeckColour = new Color(0.47f, 0.38f, 0.28f);
        private static readonly Color HullColour = new Color(0.28f, 0.36f, 0.44f);

        /// <summary>
        /// Half the harbour mouth, in degrees off the seaward axis.
        ///
        /// <para>Twenty-two rather than fifteen. At fifteen the two arms left a seventy-metre gap in a
        /// three-hundred-metre ring, and from the promenade that does not read as a harbour mouth — it
        /// reads as an atoll with a nick in it. The eye needs the arms to be arms.</para>
        /// </summary>
        private const float MouthHalfAngle = 22f;

        /// <summary>
        /// Where the quay gives way to the moles: the angle at which a mole's own arc meets the beach.
        ///
        /// <para><b>Derived, and the first version was typed.</b> At a fixed forty-two degrees the moles
        /// began thirty-five metres out in the water — two arcs floating off the coast with a gap between
        /// them and the shore, which reads as an atoll and not as a harbour. Where an arm can start is
        /// not a matter of taste: it is where its arc crosses the waterline, and that follows the
        /// shoreline about whenever the sea is retuned.</para>
        ///
        /// <para>The quay is carried out to the same angle so the two meet rather than overlap. Its last
        /// stretch stands in shallow water, which is what a quay is for.</para>
        /// </summary>
        private static float MoleRootAngle(in HarbourSite site)
        {
            float reach = site.Radius + MoleOffset;
            if (reach < 0.01f)
            {
                return 45f;
            }

            return Mathf.Acos(Mathf.Clamp(site.LandwardShore / reach, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>How far outside the basin's rim the moles stand, metres.</summary>
        private const float MoleOffset = 18f;

        /// <summary>The tints, one per submesh. Null keeps a submesh's own material.</summary>
        public static Color?[] Tints()
        {
            var tints = new Color?[SubmeshCount];
            tints[StoneSubmesh] = StoneColour;
            tints[CopingSubmesh] = CopingColour;
            tints[DeckSubmesh] = DeckColour;
            tints[HullSubmesh] = HullColour;
            return tints;
        }

        /// <summary>
        /// Where the harbour sits and what it is built against. Everything else is derived from this.
        /// </summary>
        public readonly struct HarbourSite
        {
            /// <summary>Centre of the dredged basin, at the water's surface.</summary>
            public readonly Vector3 Centre;

            /// <summary>Radius of the basin, metres.</summary>
            public readonly float Radius;

            /// <summary>Unit vector from the basin's centre towards the town.</summary>
            public readonly Vector3 Landward;

            /// <summary>Unit vector along the shore, so <c>Landward × Up</c>.</summary>
            public readonly Vector3 Alongshore;

            /// <summary>Height of the water.</summary>
            public readonly float SurfaceY;

            /// <summary>Height of the bed at the middle of the basin.</summary>
            public readonly float BedY;

            /// <summary>Height of the quay's paving — the levelled ground the town stands on.</summary>
            public readonly float QuayY;

            /// <summary>
            /// How far the natural waterline lies from the basin's centre, towards the town.
            ///
            /// <para>What the moles need in order to know where they may start: an arm has to spring
            /// from the beach, and the beach is here. See <see cref="MoleRootAngle"/>.</para>
            /// </summary>
            public readonly float LandwardShore;

            public HarbourSite(
                Vector3 centre, float radius, Vector3 landward, float surfaceY, float bedY, float quayY,
                float landwardShore)
            {
                LandwardShore = landwardShore;
                Centre = new Vector3(centre.x, surfaceY, centre.z);
                Radius = radius;
                Landward = new Vector3(landward.x, 0f, landward.z).normalized;
                Alongshore = Vector3.Cross(Vector3.up, Landward).normalized;
                SurfaceY = surfaceY;
                BedY = bedY;
                QuayY = quayY;
            }

            /// <summary>A point on a circle about the basin's centre. Zero degrees faces land.</summary>
            public Vector3 At(float degrees, float radius, float y)
            {
                float radians = degrees * Mathf.Deg2Rad;
                Vector3 plan = Centre
                               + Landward * (Mathf.Cos(radians) * radius)
                               + Alongshore * (Mathf.Sin(radians) * radius);

                return new Vector3(plan.x, y, plan.z);
            }
        }

        /// <summary>
        /// The whole harbour, into one buffer.
        ///
        /// <para>Ordered as it would be built: the wall that holds the land back, the arms that keep the
        /// sea out, the light on the end of one of them, and then the things that float.</para>
        /// </summary>
        public static void AddHarbour(VegetationMeshBuffer buffer, in HarbourSite site)
        {
            float root = MoleRootAngle(site);

            AddQuay(buffer, site, root);

            AddMole(buffer, site, root, 180f - MouthHalfAngle);
            AddMole(buffer, site, -root, -(180f - MouthHalfAngle));

            // On the head of one arm only. Two lights facing each other across a seventy-metre mouth is
            // what a commercial port has; a town harbour has one, and one vertical on a horizon is worth
            // more than two.
            Vector3 head = site.At(180f - MouthHalfAngle, site.Radius + MoleOffset, MoleCrestY(site));
            AddLighthouse(buffer, site, head);

            AddPontoons(buffer, site);
        }

        /// <summary>Height of a mole's crest. Well clear of the water, and level along its length.</summary>
        private static float MoleCrestY(in HarbourSite site)
        {
            return site.SurfaceY + 2.4f;
        }

        /// <summary>
        /// The quay: a vertical wall from the bed to the paving, with a coping along the top and bollards
        /// on it.
        ///
        /// <para>Vertical because that is what a quay is for — a boat comes alongside it. The terrain
        /// under it is a six-metre bank rather than a wall (<c>SeeburgCourse.BasinBankEase</c>), which is
        /// as steep as a height field can usefully be made; the wall stands over that bank and hides it,
        /// which is the whole reason the bank was made narrow rather than eased.</para>
        /// </summary>
        private static void AddQuay(VegetationMeshBuffer buffer, in HarbourSite site, float halfAngle)
        {
            const int segments = 26;
            const float copingHeight = 0.35f;
            const float copingOut = 0.4f;
            const float apronDepth = 5f;

            float top = site.QuayY;
            float foot = site.BedY;

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.Lerp(-halfAngle, halfAngle, i / (float)segments);
                float a1 = Mathf.Lerp(-halfAngle, halfAngle, (i + 1) / (float)segments);

                Vector3 outward0 = (site.At(a0, 1f, 0f) - site.At(a0, 0f, 0f)).normalized;
                Vector3 outward1 = (site.At(a1, 1f, 0f) - site.At(a1, 0f, 0f)).normalized;
                Vector3 outward = ((outward0 + outward1) * 0.5f).normalized;

                // The wall face, looking out over the water.
                Vector3 f0 = site.At(a0, site.Radius, foot);
                Vector3 f1 = site.At(a1, site.Radius, foot);
                Vector3 t0 = site.At(a0, site.Radius, top - copingHeight);
                Vector3 t1 = site.At(a1, site.Radius, top - copingHeight);

                buffer.AddQuadFacing(StoneSubmesh, f0, f1, t1, t0, -outward);

                // The coping: a band standing proud of the wall, so the edge has a shadow line under it
                // rather than simply stopping.
                Vector3 c0 = site.At(a0, site.Radius - copingOut, top - copingHeight);
                Vector3 c1 = site.At(a1, site.Radius - copingOut, top - copingHeight);
                Vector3 h0 = site.At(a0, site.Radius - copingOut, top);
                Vector3 h1 = site.At(a1, site.Radius - copingOut, top);

                buffer.AddQuadFacing(CopingSubmesh, c0, c1, h1, h0, -outward);

                Vector3 b0 = site.At(a0, site.Radius + apronDepth, top);
                Vector3 b1 = site.At(a1, site.Radius + apronDepth, top);

                buffer.AddQuadFacing(CopingSubmesh, h0, h1, b1, b0, Vector3.up);

                // Bollards every fourth segment — about one every thirty metres, which is a berth.
                if (i % 4 == 2)
                {
                    float middle = (a0 + a1) * 0.5f;
                    Vector3 at = site.At(middle, site.Radius + 1.8f, top);
                    AddPost(buffer, CopingSubmesh, at, 0.28f, 0.75f);
                }
            }
        }

        /// <summary>
        /// One mole: a rubble mound with a walkable crest, swept along an arc outside the basin's rim.
        ///
        /// <para>A trapezoid rather than a wall, because that is what a breakwater is and because the
        /// slope is what makes it read as one from the promenade — a vertical face at this distance is
        /// indistinguishable from a pier.</para>
        /// </summary>
        private static void AddMole(
            VegetationMeshBuffer buffer, in HarbourSite site, float fromDegrees, float toDegrees)
        {
            const int segments = 18;
            const float baseHalf = 9f;
            const float crestHalf = 3.4f;

            float radius = site.Radius + MoleOffset;
            float crest = MoleCrestY(site);
            float foot = site.BedY;

            // Copied out because the two helpers below are local functions and C# will not let one close
            // over an `in` parameter. The struct is readonly, so the copy is the same harbour.
            HarbourSite where = site;

            Vector3 Base(float degrees, float offset)
            {
                return where.At(degrees, radius + offset, foot);
            }

            Vector3 Crest(float degrees, float offset)
            {
                return where.At(degrees, radius + offset, crest);
            }

            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.Lerp(fromDegrees, toDegrees, i / (float)segments);
                float a1 = Mathf.Lerp(fromDegrees, toDegrees, (i + 1) / (float)segments);

                Vector3 outward = (site.At(a0, 1f, 0f) - site.At(a0, 0f, 0f)).normalized;

                // Outer slope, taking the sea.
                buffer.AddQuadFacing(StoneSubmesh,
                    Base(a0, baseHalf), Base(a1, baseHalf),
                    Crest(a1, crestHalf), Crest(a0, crestHalf), outward);

                // Inner slope, facing the harbour.
                buffer.AddQuadFacing(StoneSubmesh,
                    Base(a1, -baseHalf), Base(a0, -baseHalf),
                    Crest(a0, -crestHalf), Crest(a1, -crestHalf), -outward);

                // The crest walk.
                buffer.AddQuadFacing(CopingSubmesh,
                    Crest(a0, -crestHalf), Crest(a1, -crestHalf),
                    Crest(a1, crestHalf), Crest(a0, crestHalf), Vector3.up);
            }

            // The head, closed off. An open end is a hole you can see the inside of the world through.
            Vector3 alongHead = (site.At(toDegrees, radius, 0f) - site.At(toDegrees - 1f, radius, 0f))
                .normalized;

            buffer.AddQuadFacing(StoneSubmesh,
                Base(toDegrees, -baseHalf), Base(toDegrees, baseHalf),
                Crest(toDegrees, crestHalf), Crest(toDegrees, -crestHalf), alongHead);
        }

        /// <summary>
        /// The lighthouse: a tapered octagonal tower, a gallery, and a lantern that lights after dark.
        ///
        /// <para>Octagonal rather than round, and tapered rather than straight. Eight sides at this size
        /// is four more than reads as a box and eight fewer than costs anything; the taper is what tells
        /// it apart from the chimney it would otherwise be, which is exactly the argument
        /// <see cref="LandmarkMeshes"/> makes about its minaret.</para>
        ///
        /// <para>Bands rather than stripes: two rings of coping, at a third and two thirds. A real
        /// lighthouse's spiral is paint, and paint is not something a flat-shaded low-poly world has.</para>
        /// </summary>
        private static void AddLighthouse(VegetationMeshBuffer buffer, in HarbourSite site, Vector3 head)
        {
            const int sides = 8;
            const float footRadius = 3.1f;
            const float neckRadius = 1.9f;
            const float galleryRadius = 2.6f;

            float baseY = head.y;
            float neckY = baseY + LighthouseHeight - 4.2f;

            // A plinth, so the tower stands on the mole rather than growing out of it.
            AddPrism(buffer, StoneSubmesh, head, sides, footRadius + 0.6f, footRadius + 0.6f,
                baseY - 0.6f, baseY + 0.8f);

            AddPrism(buffer, StoneSubmesh, head, sides, footRadius, neckRadius, baseY + 0.8f, neckY);

            AddPrism(buffer, CopingSubmesh, head, sides,
                Mathf.Lerp(footRadius, neckRadius, 0.34f) + 0.15f,
                Mathf.Lerp(footRadius, neckRadius, 0.34f) + 0.15f,
                baseY + (neckY - baseY) * 0.34f, baseY + (neckY - baseY) * 0.34f + 0.9f);

            AddPrism(buffer, CopingSubmesh, head, sides,
                Mathf.Lerp(footRadius, neckRadius, 0.67f) + 0.15f,
                Mathf.Lerp(footRadius, neckRadius, 0.67f) + 0.15f,
                baseY + (neckY - baseY) * 0.67f, baseY + (neckY - baseY) * 0.67f + 0.9f);

            // The gallery: a walkway standing wider than the shaft it rings. Without it the lantern is a
            // cap on a post.
            AddPrism(buffer, CopingSubmesh, head, sides, galleryRadius, galleryRadius, neckY, neckY + 0.5f);

            // The lantern. Its own submesh, and the only part of the harbour that changes after dark.
            AddPrism(buffer, LanternSubmesh, head, sides, neckRadius, neckRadius,
                neckY + 0.5f, neckY + 3f);

            // A cap over it, or the light is a lamp on a stick.
            AddPrism(buffer, StoneSubmesh, head, sides, neckRadius + 0.35f, 0.2f,
                neckY + 3f, neckY + 4.2f);
        }

        /// <summary>
        /// The floating jetties inside the basin, on the sheltered side, and the boats on them.
        ///
        /// <para>Set along the shore rather than radially. A pontoon runs the way the boats lie, boats
        /// lie head to wind, and wind in a harbour comes through the mouth — so a finger pointing back at
        /// the town is the one arrangement that would have every hull broadside to the weather.</para>
        /// </summary>
        private static void AddPontoons(VegetationMeshBuffer buffer, in HarbourSite site)
        {
            float deck = site.SurfaceY + 0.2f;

            // Two, at a third and two thirds of the way out. Any closer to the quay and the moorings are
            // under the wall; any further and they are in the fairway.
            for (int p = 0; p < 2; p++)
            {
                float out0 = site.Radius * (0.30f + p * 0.26f);
                float halfLength = site.Radius * (0.42f - p * 0.08f);

                Vector3 middle = site.Centre + site.Landward * out0;
                Vector3 a = middle - site.Alongshore * halfLength;
                Vector3 b = middle + site.Alongshore * halfLength;

                AddBeam(buffer, DeckSubmesh, a, b, 1.6f, deck, 0.35f);

                int piles = 6;
                for (int i = 0; i <= piles; i++)
                {
                    Vector3 at = Vector3.Lerp(a, b, i / (float)piles);
                    AddPost(buffer, CopingSubmesh,
                        new Vector3(at.x, deck + 0.35f, at.z), 0.16f, 0.9f);

                    // Boats down alternate sides, so the pontoon is used rather than decorated.
                    if (i < piles)
                    {
                        Vector3 berth = Vector3.Lerp(a, b, (i + 0.5f) / piles);
                        float side = (i % 2 == 0) ? 1f : -1f;
                        AddBoat(buffer, site,
                            berth + site.Landward * (side * 4.6f),
                            site.Alongshore, 3.2f + (i % 3) * 0.9f);
                    }
                }
            }
        }

        /// <summary>
        /// One moored boat: a hull that tapers at both ends, a low cabin, and a mast on the longer ones.
        ///
        /// <para>Six triangles' worth of taper is all it takes. A box floating at the waterline reads as a
        /// crate; the same box with its ends drawn to a point reads as a boat at any distance a player
        /// ever sees one of these from.</para>
        /// </summary>
        private static void AddBoat(
            VegetationMeshBuffer buffer, in HarbourSite site, Vector3 at, Vector3 heading, float halfLength)
        {
            float waterline = site.SurfaceY;
            float deck = waterline + 0.85f;
            float draught = waterline - 0.55f;
            float halfBeam = halfLength * 0.34f;

            Vector3 forward = new Vector3(heading.x, 0f, heading.z).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            Vector3 bow = at + forward * halfLength;
            Vector3 stern = at - forward * halfLength;

            Vector3 keelBow = new Vector3(bow.x, draught, bow.z);
            Vector3 keelStern = new Vector3(stern.x, draught, stern.z);

            Vector3 portFore = at + forward * (halfLength * 0.45f) - right * halfBeam;
            Vector3 portAft = at - forward * (halfLength * 0.55f) - right * halfBeam;
            Vector3 starFore = at + forward * (halfLength * 0.45f) + right * halfBeam;
            Vector3 starAft = at - forward * (halfLength * 0.55f) + right * halfBeam;

            Vector3 Deck(Vector3 v) => new Vector3(v.x, deck, v.z);

            // Topsides, port and starboard, each a quad amidships and a triangle at either end.
            buffer.AddQuadFacing(HullSubmesh, Deck(portAft), Deck(portFore),
                new Vector3(portFore.x, draught, portFore.z),
                new Vector3(portAft.x, draught, portAft.z), -right);
            buffer.AddQuadFacing(HullSubmesh, Deck(starFore), Deck(starAft),
                new Vector3(starAft.x, draught, starAft.z),
                new Vector3(starFore.x, draught, starFore.z), right);

            buffer.AddTriangleFacing(HullSubmesh, Deck(bow), Deck(portFore),
                new Vector3(portFore.x, draught, portFore.z), -right + forward);
            buffer.AddTriangleFacing(HullSubmesh, Deck(starFore), Deck(bow),
                new Vector3(starFore.x, draught, starFore.z), right + forward);
            buffer.AddTriangleFacing(HullSubmesh, Deck(portAft), Deck(stern),
                new Vector3(portAft.x, draught, portAft.z), -right - forward);
            buffer.AddTriangleFacing(HullSubmesh, Deck(stern), Deck(starAft),
                new Vector3(starAft.x, draught, starAft.z), right - forward);

            buffer.AddTriangleFacing(HullSubmesh, keelBow, keelStern,
                new Vector3(portAft.x, draught, portAft.z), -Vector3.up);
            buffer.AddTriangleFacing(HullSubmesh, keelStern, keelBow,
                new Vector3(starAft.x, draught, starAft.z), -Vector3.up);

            // The deck, as one quad plus the two end triangles that close it.
            buffer.AddQuadFacing(DeckSubmesh,
                Deck(portAft), Deck(portFore), Deck(starFore), Deck(starAft), Vector3.up);
            buffer.AddTriangleFacing(DeckSubmesh, Deck(portFore), Deck(bow), Deck(starFore), Vector3.up);
            buffer.AddTriangleFacing(DeckSubmesh, Deck(starAft), Deck(stern), Deck(portAft), Vector3.up);

            // A cabin set aft of amidships, and a mast on anything big enough to carry one.
            Vector3 cabin = at - forward * (halfLength * 0.15f);
            AddBeam(buffer, DeckSubmesh,
                cabin - forward * (halfLength * 0.22f), cabin + forward * (halfLength * 0.22f),
                halfBeam * 0.62f, deck, 0.95f);

            // A mast on the biggest only, and shorter than a real one.
            //
            // Every boat carrying two lengths of mast turned the moorings into a picket fence: from the
            // promenade the harbour was a row of vertical lines with the hulls lost under them, which is
            // the opposite of what the masts were for. One in three, at one and a third lengths, reads as
            // a marina; all of them at two reads as scaffolding.
            if (halfLength > 4.5f)
            {
                AddPost(buffer, CopingSubmesh,
                    new Vector3(cabin.x, deck, cabin.z) + forward * (halfLength * 0.35f),
                    0.11f, halfLength * 1.35f);
            }
        }

        /// <summary>
        /// The promenade rail along the seaward kerb of the waterfront, with a bench every so often.
        ///
        /// <para>Laid against the ground rather than at a level, because it runs the whole length of the
        /// front and the front has a cross-fall on it. Sampling is what every other linear builder in the
        /// world does — see <c>GuardRailBuilder</c> — and for the same reason: a straight rail on sloping
        /// ground is buried at one end and floating at the other.</para>
        /// </summary>
        public static void AddPromenade(
            VegetationMeshBuffer buffer,
            IRoadPath axis,
            MountainField field,
            StreetNetwork streets,
            float fromAlong,
            float toAlong,
            float across,
            float clearance,
            float maxSwing,
            out float worstSwing,
            out int gaps)
        {
            worstSwing = 0f;
            gaps = 0;

            const float postSpacing = 3.2f;
            const float railHeight = 1.05f;
            const float benchSpacing = 46f;

            int posts = Mathf.Max(2, Mathf.RoundToInt((toAlong - fromAlong) / postSpacing));

            Vector3 previousTop = Vector3.zero;
            bool hasPrevious = false;

            for (int i = 0; i <= posts; i++)
            {
                float along = Mathf.Lerp(fromAlong, toAlong, i / (float)posts);
                float clamped = Mathf.Clamp(along, 0f, axis.Length);

                Vector3 centre = axis.GetPositionAtDistance(clamped);
                Vector3 right = axis.GetRightAtDistance(clamped);

                // Across is negative seaward, and the axis' right is inland — see SeeburgCourse.
                float out0 = ClearOfPaving(streets, centre, right, across, clearance, maxSwing);

                // Where the paving reaches further out than the rail may go, there is simply no rail.
                //
                // <b>A gap, not a bigger bulge, and the numbers force it.</b> A junction pad on the
                // boulevard fans out about twenty-five metres to seaward — it is paved ground with no
                // street on that side, so the fan has nothing to stop against — while the beach begins
                // twenty metres out. A rail that followed that outline would stand in the sand, and past
                // the harbour it would stand in the water. Every seafront ever built does the same thing
                // here: the railing stops at the side road and starts again after it.
                if (float.IsNaN(out0))
                {
                    gaps++;
                    previousTop = Vector3.zero;
                    hasPrevious = false;
                    continue;
                }

                worstSwing = Mathf.Max(worstSwing, across - out0);

                Vector3 at = centre + right * out0;
                at.y = field.HeightAt(at.x, at.z);

                AddPost(buffer, CopingSubmesh, at, 0.07f, railHeight);

                Vector3 top = new Vector3(at.x, at.y + railHeight, at.z);
                if (hasPrevious)
                {
                    AddBeam(buffer, CopingSubmesh, previousTop, top, 0.05f, top.y - 0.09f, 0.09f);
                }

                previousTop = top;
                hasPrevious = true;

                // A bench on the paved side of the rail, unless the rail has had to swing out around a
                // junction here — in which case the paved side is the junction.
                if (hasPrevious && (along - fromAlong) % benchSpacing < postSpacing
                    && Mathf.Approximately(out0, across))
                {
                    Vector3 seat = centre + right * (across + 3.4f);
                    seat.y = field.HeightAt(seat.x, seat.z);
                    AddBench(buffer, seat, Vector3.Cross(Vector3.up, right));
                }
            }
        }

        /// <summary>
        /// How far out the rail has to stand at this station to be clear of any junction's paving.
        ///
        /// <para><b>The rail follows the pads; it does not cut through them.</b> Held at one offset from
        /// the boulevard it ran straight across the middle of every junction on the seafront — those pads
        /// are round, they are paved, and they are driven on, so a line of railing through the middle of
        /// one is a fence across a road. Swung out around each in turn, the same rail reads the way a
        /// promenade actually does: a kerb line that bulges wherever a street meets it.</para>
        ///
        /// <para>Pushed straight out rather than swept radially round the node, which was the other
        /// candidate. Radially is the shape a kerb really takes, but it moves a post <i>along</i> the
        /// front as well as out, and posts that slide past one another cross their own hand rail. Holding
        /// the station and moving only the offset keeps the rail monotone, and against a pad boundary
        /// that is star-shaped about its node it traces the same outline.</para>
        /// </summary>
        private static float ClearOfPaving(
            StreetNetwork streets, Vector3 centre, Vector3 right, float across, float clearance,
            float maxSwing)
        {
            const float step = 0.75f;

            if (streets == null)
            {
                return across;
            }

            float at = across;

            for (float pushed = 0f; pushed <= maxSwing; pushed += step)
            {
                at = across - pushed;
                Vector3 point = centre + right * at;

                // The ribbons first, and they are the half of this that is easy to miss. Every street in
                // Seeburg bows — the boulevard by two or three metres either way — so a rail held
                // straight against the axis wanders onto the footway at mid-span even where there is no
                // junction within a hundred metres.
                //
                // <b>The boulevard's ribbons only, and each against its own width.</b> The first version
                // asked StreetIndex whether <i>any</i> street ran within the widest half-section in the
                // town, which is one lookup and answers the wrong question twice over: it pushes the rail
                // out for the seven-metre lanes running inland off the front, which it never needed to
                // clear, and it measures them all as if they were the boulevard. Two thirds of the posts
                // came out swung, up to twenty-five metres — a rail standing on the beach, which is not a
                // promenade either.
                bool blocked = false;

                for (int e = 0; e < streets.Edges.Count && !blocked; e++)
                {
                    StreetEdge edge = streets.Edges[e];

                    if (edge.Kind != TownStreetKind.Boulevard || edge.Path == null)
                    {
                        continue;
                    }

                    blocked = PlanDistance(edge.Path, point) < edge.HalfOuter + clearance;
                }

                for (int n = 0; n < streets.Nodes.Count && !blocked; n++)
                {
                    StreetNode node = streets.Nodes[n];

                    float dx = point.x - node.Position.x;
                    float dz = point.z - node.Position.z;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);

                    // The rejection first: a pad reaches tens of metres, never hundreds, and this runs
                    // for every post against every node in the town.
                    if (distance > 60f)
                    {
                        continue;
                    }

                    blocked = distance < node.PavingReach(point) + clearance;
                }

                if (!blocked)
                {
                    return at;
                }
            }

            // Not a number rather than a best effort: the caller has to be able to tell "here it is" from
            // "there is no room for it here", and any float it got back would be the first.
            return float.NaN;
        }

        /// <summary>
        /// Plan distance from a point to the nearest place on a path. Sampled, because a street is a
        /// Catmull-Rom ribbon and there is no closed form for the nearest point on one.
        /// </summary>
        private static float PlanDistance(IRoadPath path, Vector3 point)
        {
            const float step = 4f;

            float best = float.MaxValue;

            for (float at = 0f; at <= path.Length; at += step)
            {
                Vector3 on = path.GetPositionAtDistance(at);
                float dx = on.x - point.x;
                float dz = on.z - point.z;

                best = Mathf.Min(best, dx * dx + dz * dz);
            }

            return Mathf.Sqrt(best);
        }

        /// <summary>A bench: a slab on two legs, facing the water.</summary>
        private static void AddBench(VegetationMeshBuffer buffer, Vector3 at, Vector3 alongshore)
        {
            Vector3 along = new Vector3(alongshore.x, 0f, alongshore.z).normalized;

            AddBeam(buffer, DeckSubmesh, at - along * 0.85f, at + along * 0.85f, 0.28f, at.y + 0.42f, 0.08f);
            AddPost(buffer, CopingSubmesh, at - along * 0.7f, 0.07f, 0.42f);
            AddPost(buffer, CopingSubmesh, at + along * 0.7f, 0.07f, 0.42f);
        }

        /// <summary>A square post standing on a point. Bollards, piles, rail posts, masts.</summary>
        private static void AddPost(
            VegetationMeshBuffer buffer, int submesh, Vector3 foot, float half, float height)
        {
            var place = new PlantPlacement(foot, Vector3.up, 0f, 1f, 0u);
            BuildingMeshes.AddBox(buffer, place, submesh, 0f, 0f, 0f, half, height, half);
        }

        /// <summary>
        /// A box swept between two points at a fixed height — decking, rails, seats.
        ///
        /// <para><paramref name="baseY"/> rather than the endpoints' own heights, because these are all
        /// things that are level by definition: a pontoon floats and a hand rail does not follow the
        /// paving under it stair by stair.</para>
        /// </summary>
        private static void AddBeam(
            VegetationMeshBuffer buffer, int submesh, Vector3 from, Vector3 to,
            float half, float baseY, float thickness)
        {
            Vector3 along = to - from;
            along.y = 0f;

            float length = along.magnitude;
            if (length < 0.01f)
            {
                return;
            }

            Vector3 middle = (from + to) * 0.5f;
            middle.y = baseY;

            float yaw = Mathf.Atan2(along.x, along.z);
            var place = new PlantPlacement(middle, Vector3.up, yaw, 1f, 0u);

            BuildingMeshes.AddBox(buffer, place, submesh, 0f, 0f, 0f, half, thickness, length * 0.5f);
        }

        /// <summary>
        /// A vertical prism with as many sides as asked for, tapering between two radii.
        ///
        /// <para>Its own rather than <c>LandmarkMeshes.AddPrism</c>, which is private to that file and
        /// takes a <c>PlantPlacement</c> the harbour has no use for — everything here is already in world
        /// space, laid against an arc rather than against a plot.</para>
        /// </summary>
        private static void AddPrism(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 centre,
            int sides,
            float bottomRadius,
            float topRadius,
            float bottomY,
            float topY)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                Vector3 d0 = new Vector3(Mathf.Sin(a0), 0f, Mathf.Cos(a0));
                Vector3 d1 = new Vector3(Mathf.Sin(a1), 0f, Mathf.Cos(a1));

                Vector3 b0 = centre + d0 * bottomRadius;
                Vector3 b1 = centre + d1 * bottomRadius;
                Vector3 t0 = centre + d0 * topRadius;
                Vector3 t1 = centre + d1 * topRadius;

                b0.y = bottomY;
                b1.y = bottomY;
                t0.y = topY;
                t1.y = topY;

                buffer.AddQuadFacing(submesh, b0, b1, t1, t0, (d0 + d1).normalized);

                // The lid. No floor: every prism here stands on something.
                if (topRadius > 0.01f)
                {
                    Vector3 apex = new Vector3(centre.x, topY, centre.z);
                    buffer.AddTriangleFacing(submesh, apex, t0, t1, Vector3.up);
                }
            }
        }
    }
}
