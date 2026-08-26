using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The road on from the crossing: the eastern cape, the bay behind it, the harbour village of
    /// Yalıköy on its shore, and the climb into the dry hills above.
    ///
    /// <para><b>Why there had to be something here at all.</b> The Meerenge stopped eleven hundred
    /// metres past the eastern anchorage, on a falling straight, in the middle of a hillside. A bridge
    /// is a threshold, and a threshold with nothing behind it is a long piece of road — the far shore
    /// had a country of its own (<see cref="LandRegion.Anadolu"/>) and nothing in it. This is what the
    /// crossing is for.</para>
    ///
    /// <para><b>The sea here is not the strait.</b> <c>Boğaz</c> is a corridor river laid across the
    /// bridge, 600 m half-width and 2600 m of reach, and it runs roughly north–south some two and a
    /// half kilometres west of where this road begins. The bay is a body of its own on the other side
    /// of <see cref="CapeName"/> — which is what the cape is for, and why the tunnel through it is the
    /// only place on this road where there is no water on either hand. Keeping them separate is not
    /// tidiness: a <c>Sea</c> <i>sets</i> the ground under it and the river only caps it, so two of
    /// them over the same ground fight, and the loser leaves a step across the middle of the
    /// water.</para>
    ///
    /// <para><b>The seafront is dead straight, and that is a structural requirement rather than a
    /// styling one.</b> Yalıköy hangs off this road directly — no axis of its own, the way Talheim
    /// hangs off the pass — and town-local coordinates fold where the trunk road bends towards the town
    /// more tightly than the town is deep: <c>TownShape.LimitAcross</c> caps a town at 0.65·R. Three
    /// hundred metres of inland town would need a 460 m radius; on a straight there is no radius at
    /// all. <c>ValidateTownMapping</c> measures it rather than trusting this comment.</para>
    ///
    /// <para><b>Nothing is revealed from more than about half a kilometre.</b> The camera's far plane
    /// is 600 m and the fog wall stands inside it — the lesson the Kalkgrattunnel had to learn the
    /// expensive way. So the bay arrives on the corner after the cape and not before it, and the bridge
    /// is not seen again until the hills are high enough to look back over the whole strait from
    /// <see cref="LookbackName"/>.</para>
    ///
    /// <para><b>The climb takes its steepness from its legs.</b> Short legs stacked close together make
    /// a steep face; open corners do not. The hairpins here are 46 to 54 m with 120 to 140 m between
    /// them, which is the Steilufer's rule at half the scale — this is a track up a dry hillside behind
    /// a fishing village, not a pass.</para>
    ///
    /// <para>It ends provisionally, the way the Ebental, the Kalkgrat and the Meerenge all do.</para>
    /// </summary>
    public static class YalikoyCourse
    {
        /// <summary>The bore through the cape between the strait and the bay.</summary>
        public const string CapeName = "Kızılkaya Tüneli";

        /// <summary>The viewpoint the bay opens from.</summary>
        public const string BayViewName = "Koy Bakışı";

        /// <summary>
        /// The viewpoint on the climb, which looks back down over the bay.
        ///
        /// <para><b>Not over the crossing, and it cannot be.</b> The bridge is four kilometres from the
        /// hills behind this village, the camera's far plane is 600 m and the fog wall stands inside it
        /// — the Kalkgrattunnel's lesson, which this leg was on course to repeat under the name Köprü
        /// Manzarası. What is in frame from where it now stands is the village strung along its own
        /// water with the harbour in the middle of it, and that is worth stopping for.</para>
        /// </summary>
        public const string LookbackName = "Yalı Manzarası";

        /// <summary>The village.</summary>
        public const string TownName = "Yalıköy";

        // --- The bay.

        /// <summary>
        /// Radius of the bay, metres.
        ///
        /// <para>Smaller than the Westmeer's 2600 because this is a koy rather than an open sea, and
        /// small enough that its rim curves visibly along the front: sixty metres of bulge over the
        /// eight hundred of seafront, which is the difference between a bay and a straight edge. What
        /// pays for that is <see cref="Seaward"/> — every metre the waterline wanders is levelled apron
        /// somebody has to lay.</para>
        /// </summary>
        public const float BayRadius = 1600f;

        /// <summary>
        /// How far in from the shore the water reaches full depth. See <c>WaterBody.BedScale</c>.
        ///
        /// <para>Untied from the radius for the reason <c>SeeburgCourse.SeaBedScale</c> gives: the tile
        /// builder reads the carved bed back to decide how dark the water is, so a dish spread over the
        /// whole radius is a bay that is uniformly pale for every metre of it anyone can see.</para>
        ///
        /// <para><b>160, not 400.</b> At four hundred the first three hundred metres off the beach never
        /// got deeper than a puddle looks, and from the seafront the bay read as a pale shoal running
        /// out to a haze — a lagoon with a lighthouse in it. This bay is smaller than the Westmeer and
        /// its dish has to be tighter in proportion, or the only part of it anyone ever sees is the part
        /// with no colour in it.</para>
        /// </summary>
        public const float BayBedScale = 160f;

        /// <summary>Depth of the bay at its darkest, metres.</summary>
        public const float BayDepth = 12f;

        /// <summary>Width of the beach — the band outside the disc drawn down to the waterline.</summary>
        public const float BayBankEase = 20f;

        /// <summary>How far the water sits below the seafront, metres.</summary>
        public const float SeaFreeboard = 3.5f;

        /// <summary>
        /// How far seaward of the road the waterline sits at the middle of the front, metres.
        ///
        /// <para><b>Thirty, and the first answer was forty with the basin further out again.</b> That put
        /// the harbour two hundred metres off the road across a flat apron of dry scrub, and the picture
        /// from the seafront came back as a lane through a heath with a lighthouse on the horizon. A
        /// fishing village has its quay against its road — the boats are the frontage. See
        /// <see cref="BasinAcross"/>, which had to come in with it: the basin's landward rim has to stay
        /// inside the natural waterline or the moles spring from open water.</para>
        /// </summary>
        public const float ShoreOffset = 30f;

        // --- The harbour, in town-local terms: metres along the course and metres across it.

        /// <summary>
        /// Where the basin's centre sits, metres seaward of the road.
        ///
        /// <para>Sized so its landward rim stands 25 m out — the road's shoulder, a promenade rail and
        /// a quay apron fit between, and nothing else does. Two conditions bind it and they pull
        /// opposite ways: the rim must clear the carriageway, and
        /// <c>|BasinAcross| − <see cref="ShoreOffset"/></c> must stay under
        /// <see cref="BasinRadius"/> or the moles begin out in open water instead of springing from the
        /// beach.</para>
        /// </summary>
        public const float BasinAcross = -155f;

        public const float BasinRadius = 130f;

        /// <summary>
        /// Depth of the basin and the width of its bank.
        ///
        /// <para>The bank is almost nothing because a quay is a vertical wall: one cell of steep, with
        /// the quay geometry standing over the seam. An eased bank would be a beach inside the
        /// harbour.</para>
        /// </summary>
        public const float BasinDepth = 6f;

        /// <summary>See <see cref="BasinDepth"/>.</summary>
        public const float BasinBankEase = 6f;

        // --- The town's basin.

        /// <summary>How far the village reaches inland of the road, metres.</summary>
        public const float Inland = 300f;

        /// <summary>
        /// How far the levelled basin reaches seaward of the road, metres.
        ///
        /// <para><b>Far wider than anything built on it, and that is the job of it.</b> Only the first
        /// forty metres carry a quay; the rest is flat ground that ends up under water. It has to be
        /// there because of the order <c>MountainField.HeightAt</c> works in — the shelf blends out to
        /// natural hillside over <c>BlendDistance</c>, and if the shoreline falls outside the levelled
        /// apron the ground between the two drops below a surface that does not reach it. A dry trench
        /// at the beach, which every other check in the build is perfectly happy with.</para>
        /// </summary>
        public const float Seaward = 200f;

        /// <summary>
        /// The village's cross-fall, declared here rather than in <c>TownShape.Yalikoy</c>.
        ///
        /// <para>Same shape and same reason as <c>SeeburgCourse.CrossFallNear</c>: a harbour town climbs
        /// away from its water. Steeper than Seeburg's over the far half, because this one has a dry
        /// hillside immediately behind it rather than a valley floor.</para>
        /// </summary>
        public const float CrossFallNear = 0.010f;

        /// <summary>See <see cref="CrossFallNear"/>.</summary>
        public const float CrossFallFar = 0.022f;

        /// <summary>See <see cref="CrossFallNear"/>.</summary>
        public const float CrossFallBreak = 120f;

        private const float PortalApproach = 60f;

        /// <summary>Fall along the seafront, percent. Nearly nothing — this is a quayside.</summary>
        private const float FrontGrade = -0.15f;

        static YalikoyCourse()
        {
            var probe = new RoadCourseBuilder(MeerengeCourse.EndPoint, MeerengeCourse.EndHeading);

            Append(probe);

            RoadCourse walked = probe.Build();

            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = probe.HeadingDegrees;
        }

        /// <summary>Where this road runs out, for whatever is built on from here.</summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there. 0 faces +Z, increasing turns towards +X.</summary>
        public static float EndHeading { get; }

        /// <summary>
        /// The grade this road is still on where it runs out, in percent.
        ///
        /// <para>A constant rather than a literal in the table below, because whatever is built on from
        /// here has to open on it: two ribbons that touch and disagree about their grade meet in a step.
        /// <c>WeissjochCourse.EndGrade</c> exists for the same reason and is read by
        /// <c>WeissjochringCourse.BuildAccess</c>; this one is read by <c>BahceRingCourse</c>.</para>
        /// </summary>
        public const float EndGrade = 0.3f;

        /// <summary>Where the seafront begins — the first station the village may reach along.</summary>
        public static float CityStart { get; private set; }

        /// <summary>And where it ends.</summary>
        public static float CityEnd { get; private set; }

        /// <summary>The middle of the front, which is what the bay and the harbour are measured from.</summary>
        public static float Waterfront => (CityStart + CityEnd) * 0.5f;

        /// <summary>Where the harbour basin sits, as a station along the course.</summary>
        public static float BasinAlong => CityStart + 300f;

        /// <summary>
        /// How far the village's floor stands above the road at <paramref name="across"/> metres out.
        ///
        /// <para>The same two-slope rule <c>TownShape.CrossFall</c> applies, and it has to stay the same
        /// one — the sea's level is derived from it. There is no dish term because this town has none;
        /// see <c>TownShape.Yalikoy</c>.</para>
        /// </summary>
        public static float FloorRiseAt(float across)
        {
            float distance = Mathf.Abs(across);
            return Mathf.Min(distance, CrossFallBreak) * CrossFallNear
                   + Mathf.Max(0f, distance - CrossFallBreak) * CrossFallFar;
        }

        /// <summary>The cape, the bay, the village and the hills behind it.</summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(MeerengeCourse.EndPoint, MeerengeCourse.EndHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, so the probe in the static constructor and the real walk cannot drift
        /// apart. Two copies of a road are two roads.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- Off the far shore and out towards the point, still falling. The strait is behind the
            // left shoulder here and going further away with every metre; there is no water in any of
            // these frames and there is not meant to be.
            builder.Straight(240f, -0.8f);
            builder.Turn(460f, 34f, -1.0f);
            builder.Straight(220f, -1.0f);

            // --- Kızılkaya. Short, the way the Meerenge's two cape bores are: at a hundred and thirty
            // metres it is over in four seconds. What it is for is the shutter — everything before it is
            // the strait's shore and everything after it is the bay, and a headland driven round would
            // hand over both at once from half a kilometre out.
            builder.Straight(PortalApproach, -0.6f);
            float capeStart = builder.Distance;
            builder.Straight(130f, -0.3f);
            builder.AddFeature(RoadFeatureKind.Tunnel, capeStart, builder.Distance, CapeName);
            builder.Straight(PortalApproach, -0.6f);

            // --- Down onto the bay, converging on the shore rather than running beside it.
            //
            // <b>The first version ran parallel from here and the bay never arrived.</b> The water is a
            // disc tangent to the front, so a road that is already parallel four hundred metres short of
            // the village is two hundred and seventy metres from the waterline the whole way in — and at
            // that distance one ordinary rise on the seaward side is enough to hide it completely, which
            // is what the picture from the viewpoint came back as. Coming in at an angle and turning onto
            // the front closes that gap as the corner is taken, so the bay arrives with the turn.
            builder.Turn(380f, 40f, -1.6f);
            builder.Straight(240f, -1.4f);

            // The corner the bay arrives on. Everything before it is dry hillside and everything after it
            // is the village — see the class note about half a kilometre.
            builder.Turn(300f, -32f, -1.0f);
            builder.AddViewpoint(BayViewName);
            builder.Straight(140f, -0.6f);

            // --- The seafront. Dead straight, and every metre of it is the village. See the class note
            // for why this may not be a curve.
            CityStart = builder.Distance;
            builder.Straight(880f, FrontGrade);
            CityEnd = builder.Distance;

            // --- Out of the village and up the dry hillside behind it. The turn away from the water is
            // where the climb starts, so the last thing seen at sea level is the harbour in the mirror.
            //
            // <b>A hundred and twenty metres of straight before the bend, and the bend itself is 520 m
            // rather than 240.</b> The village's basin ends at CityEnd, and a bend that started there
            // would be a bend the town reaches across: town-local space folds where the trunk road
            // curves towards the town more tightly than the town is deep, and 300 m of Inland needs
            // 300/0.65 = 462 m of radius. ValidateTownMapping caught the first version at 0.29 of its
            // along-axis against a floor of 0.35 — which is streets authored a hundred metres apart
            // arriving on top of one another.
            builder.Straight(120f, 0.6f);

            // The layby, at the end of the front and still on it.
            //
            // <b>It was on the climb first, and from up there the harbour is behind a ridge.</b> The
            // mountain is derived from the roads, so the ground between a seafront and the track
            // climbing away from it is a shoulder — and a viewpoint on the far side of that shoulder is
            // a viewpoint of the shoulder. Here the whole village is in line down a straight, the
            // harbour is 560 m off, and both of those are inside what this world reveals. See
            // LookbackName for what it may not be pointed at.
            builder.AddViewpoint(LookbackName);

            builder.Turn(520f, 44f, 2.4f);
            builder.Straight(140f, 5.0f);

            // Three hairpins with short legs between them. Steepness comes from the legs — see the class
            // note — and 120 to 140 m at five and a half percent is a track rather than a pass.
            builder.Turn(46f, -128f, 5.5f);
            builder.Straight(130f, 5.5f);

            builder.Turn(46f, 128f, 5.5f);
            builder.Straight(120f, 5.5f);
            builder.Turn(54f, -124f, 5.0f);
            builder.Straight(140f, 4.5f);

            builder.Turn(300f, 122f, 3.2f);
            builder.Straight(180f, 2.0f);

            // --- The plateau. Landward side, so the drop stays on the driver's own window.
            builder.Turn(340f, -36f, 1.4f);
            builder.Straight(200f, 1.0f);
            builder.AddFuelStation("Yayla Benzinlik", 1f);
            builder.Straight(240f, 0.8f);
            builder.Turn(420f, 28f, 0.5f);
            builder.Straight(320f, EndGrade);
        }
    }
}
