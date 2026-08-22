using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The coast road along the Meerenge, the crossing itself, and the first stretch of the far shore.
    ///
    /// <para><b>The crossing is what the whole leg is for, and everything else is measured backwards
    /// from it.</b> The bridge is a suspension span because a viaduct on piers cannot cross open water
    /// — see <see cref="RoadFeatureKind.Suspension"/> — and the water is placed <i>by</i> the bridge
    /// rather than beside it: <c>WaterPlanner</c> lays the channel across whatever road carries the
    /// span named <see cref="BridgeName"/>, so retuning this course moves the strait with it. The
    /// constants below are therefore shared, not copied. <c>SuspensionBridgeBuilder</c> reads the
    /// structure; the water plan reads the channel; both read the same numbers.</para>
    ///
    /// <para><b>Why the main span is 950 m and not the 1074 of the bridge it is named after.</b> The
    /// camera's far plane is 600 m and the fog wall stands inside it. Past about a kilometre between
    /// the towers there is no frame anywhere on the deck that contains both of them, and a suspension
    /// bridge you cannot see the shape of is a road with railings. At 950 the towers are 475 m from
    /// the middle — hazy, which is right, and present, which is the point. This is the one dimension
    /// here taken from the renderer rather than from the reference.</para>
    ///
    /// <para><b>The corniche runs parallel to the channel, and that is not a coincidence.</b>
    /// <c>WaterPlanner.RiverSpine</c> lays the spine across the deck's own right vector, so the strait
    /// is square to the crossing plus <see cref="ChannelSkew"/>; a coast road that wandered relative to
    /// the deck's heading would open and close its distance to the water by a kilometre over its
    /// length. The last climb is deliberately taken <i>along</i> the shore rather than inland for the
    /// same reason — every metre of ramp run before the turn is a metre further the coast road has to
    /// stand back from its own coast.</para>
    ///
    /// <para><b>The names change at the water.</b> Everything west of the channel is German and
    /// everything east of it is Turkish. It costs nothing and it is most of what makes the bridge read
    /// as a threshold rather than as a long piece of road.</para>
    ///
    /// <para>It ends provisionally, the way the Ebental and the Kalkgrat both do.</para>
    /// </summary>
    public static class MeerengeCourse
    {
        /// <summary>
        /// The span's name, and the one string three separate things agree on: the feature on this
        /// course, the structure <c>SuspensionBridgeBuilder</c> looks for, and the bridge the channel
        /// is laid across by <c>WaterPlanner</c>.
        /// </summary>
        public const string BridgeName = "Boğaz Köprüsü";

        /// <summary>Whole length of the structure, anchorage to anchorage, metres.</summary>
        public const float StructureLength = 1250f;

        /// <summary>
        /// From an anchorage to the tower in front of it, metres.
        ///
        /// <para>The side spans do two jobs and the second one is easy to miss: they are what puts the
        /// anchorages on dry land. The channel is <see cref="ChannelHalfWidth"/> either side of the
        /// deck's middle, so an anchorage at 625 m out clears the waterline by 25 m — enough to stand
        /// on, and not so much that the towers end up inland of the shore they are meant to rise out
        /// of.</para>
        /// </summary>
        public const float SideSpan = 150f;

        /// <summary>Between the towers. See the class note for where the number comes from.</summary>
        public const float MainSpan = StructureLength - 2f * SideSpan;

        /// <summary>
        /// How far a tower stands above the deck, metres.
        ///
        /// <para>Measured from the deck rather than from the water, because the deck is the only datum
        /// this course knows — the water's level is solved from its own rim and is not available here.
        /// A hundred and four over a deck that sits about 56 m up puts the tower heads at roughly the
        /// 165 m of the real thing.</para>
        /// </summary>
        public const float TowerRise = 104f;

        /// <summary>
        /// Sag of the main cable below the tower heads at mid-span, metres.
        ///
        /// <para>A tenth of the main span, which is what real ones are built at and, more usefully
        /// here, is what keeps the cable clear of the deck: at 95 m of sag against 104 m of tower the
        /// cable still passes 9 m over the carriageway at its lowest. <c>ValidateSuspensionBridges</c>
        /// measures that rather than trusting this comment.</para>
        /// </summary>
        public const float CableSag = MainSpan * 0.1f;

        /// <summary>Half the open channel, metres. The strait is twice this across.</summary>
        public const float ChannelHalfWidth = 600f;

        /// <summary>
        /// Over how many metres beyond the waterline the bank climbs back to natural ground.
        ///
        /// <para>Wide, and for a reason that has nothing to do with the water: it is what the coast road
        /// looks over. At 140 m the ground stayed at road height until the last moment and then fell,
        /// which from a car 300 m back is a flat field with trees on it and no sea behind them. Easing
        /// the drop out to 240 puts most of that ground on a downward slope, and a slope is something
        /// you see over.</para>
        ///
        /// <para><b>It has to reach as far as the road, and that is a geometric condition rather than a
        /// taste.</b> The bank is a smoothstep, so it is convex in its middle: with the road 300 m back
        /// and the bank only 240 m wide, the line of sight from the driver's seat to the waterline
        /// passes <i>below</i> the bank's own crown at about half way. The ground itself was hiding the
        /// water. Once the bank reaches past the road, everything between the two is one continuous
        /// fall and there is nothing left to look over.</para>
        /// </summary>
        public const float ChannelBankEase = 340f;

        /// <summary>
        /// How far the channel runs either side of the crossing, metres.
        ///
        /// <para><b>Set by the coast road, not by the strait.</b> A corridor river only has to fill the
        /// valley its bridge spans; this one has to still be there two and a half kilometres south of
        /// the crossing, because that is where the corniche is. It was 1100 first and then 1900, and
        /// the pictures killed both: at 1100 the median distance from the coast road to the waterline
        /// was a kilometre and at 1900 it was still 500 m, and past the 600 m far plane the sea is not
        /// far away, it is absent. The shot that settled it is <c>WorldPreview_Strait_4_Corniche</c>,
        /// which came back a picture of a forest.</para>
        ///
        /// <para>The first seven hundred metres of this course is still not a coast road, and is not
        /// meant to be: it is the run-out off the Steilufer <i>reaching</i> the coast. The water arrives
        /// at the first cape and stays for the rest of the road.</para>
        /// </summary>
        public const float ChannelReach = 2600f;

        /// <summary>Degrees off square the channel crosses the deck at. A dead-square one reads as a cut.</summary>
        public const float ChannelSkew = 6f;

        public const float ChannelDepth = 55f;

        /// <summary>
        /// How far the surface sits below the lowest point of its own rim, metres.
        ///
        /// <para>Six was enough to stop the water leaking, which is what freeboard is for on a river in
        /// a valley, and nothing like enough here. Everything in this world takes its height from the
        /// roads, so the strait's rim sits at whatever height the coast road does — and with six metres
        /// of freeboard the sea came out level with the verge. Twenty-four gives the shore a fall worth
        /// the name, and the corniche something to be above.</para>
        ///
        /// <para>It is still solved rather than typed: the rim is sampled before anything is carved, so
        /// this is a promise about ground that exists.</para>
        /// </summary>
        public const float ChannelFreeboard = 24f;

        /// <summary>
        /// Over how many metres in from the edge the bed reaches full depth. See <c>WaterBody.BedScale</c>.
        ///
        /// <para>Untied from the half-width for exactly the reason a sea's is: a dish spread over 600 m
        /// is uniformly pale for the few hundred metres of it anybody ever sees from the deck. At 220 m
        /// the water is dark by the time it is under the towers, which is what a deep channel looks
        /// like.</para>
        /// </summary>
        public const float ChannelBedScale = 220f;

        /// <summary>
        /// The structure's dimensions, in the form <see cref="SuspensionBridgeBuilder"/> takes them.
        ///
        /// <para>Assembled here rather than kept there because the course is what decides whether the
        /// anchorages land on dry ground: <see cref="SideSpan"/> against
        /// <see cref="ChannelHalfWidth"/> is that decision, and both numbers are above.</para>
        /// </summary>
        public static SuspensionShape Crossing => new SuspensionShape(SideSpan, TowerRise, CableSag);

        private const float CornicheGrade = -0.4f;

        /// <summary>Grade of the short rise onto the crossing, percent.</summary>
        private const float RampGrade = 2.4f;

        private const float PortalApproach = 60f;

        static MeerengeCourse()
        {
            var probe = new RoadCourseBuilder(KalkgratCourse.EndPoint, KalkgratCourse.EndHeading);

            Append(probe);

            RoadCourse walked = probe.Build();

            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = probe.HeadingDegrees;
        }

        /// <summary>Where the far shore road runs out, for whatever is built on from here.</summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there. 0 faces +Z, increasing turns towards +X.</summary>
        public static float EndHeading { get; }

        /// <summary>Distance along the course at which the structure begins, at the western anchorage.</summary>
        public static float CrossingStart { get; private set; }

        /// <summary>Middle of the deck — the point the channel is centred on.</summary>
        public static float CrossingMiddle => CrossingStart + StructureLength * 0.5f;

        /// <summary>The coast road, the crossing and the far shore.</summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(KalkgratCourse.EndPoint, KalkgratCourse.EndHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, so the probe in the static constructor and the real walk cannot drift
        /// apart. Two copies of a road are two roads.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- The corniche. Water on the right for its whole length, rock on the left, and nothing
            // tighter than 500 m — after the Steilufer this road is meant to be somewhere the throttle
            // goes back down and stays there.
            //
            // <b>Every heading here is held within about fifteen degrees of the channel's own.</b> That
            // is not a styling rule, it is the whole reason this is a coast road: WaterPlanner lays the
            // spine square to the deck plus the skew, so the strait's axis is fixed by the crossing, and
            // a coast road that wanders relative to it opens and closes its distance to the water by
            // more than a kilometre over its length. The first version swung between −8° and +26° and
            // came back, in the pictures, as a forest with no sea in it anywhere.
            builder.Straight(260f, -0.9f);
            builder.Turn(600f, 16f, CornicheGrade);
            builder.Straight(200f, CornicheGrade);

            // Two capes, bored rather than driven round. They are short on purpose: at ninety metres a
            // bore is over in three seconds, which is exactly what a headland tunnel on a coast road is
            // and is the opposite of what the Kalkgrattunnel is for.
            builder.Straight(PortalApproach, -0.4f);
            float capeStart = builder.Distance;
            builder.Straight(90f, -0.3f);
            builder.AddFeature(RoadFeatureKind.Tunnel, capeStart, builder.Distance, "Möwenkap");
            builder.Straight(PortalApproach, -0.4f);

            builder.Turn(500f, -22f, CornicheGrade);

            // 180 + 200 around the station. Landward side — the water side of this road is the reason
            // to drive it, which is the same argument CoastCourse makes at Bucht Tankstelle.
            builder.Straight(180f, CornicheGrade);
            builder.AddFuelStation("Tankstelle Steilbucht", -1f);
            builder.Straight(200f, CornicheGrade);

            builder.Turn(520f, 20f, -0.4f);

            builder.Straight(PortalApproach, -0.3f);
            capeStart = builder.Distance;
            builder.Straight(130f, -0.2f);
            builder.AddFeature(RoadFeatureKind.Tunnel, capeStart, builder.Distance, "Felskap");
            builder.Straight(PortalApproach, -0.3f);

            builder.Turn(560f, -16f, -0.4f);
            builder.Straight(200f, -0.3f);

            // The bay. The last place on this road at eye level with the water — everything past here
            // is climbing, and the next view of the strait is from fifty-eight metres up.
            builder.AddViewpoint("Steilbucht");

            // --- Onto the deck, and it is a rise rather than a ramp. The Steilufer now stops on the
            // shelf the corniche runs along, about fifty metres over the water, so the crossing is
            // already nearly at road height and there is nothing left to climb.
            //
            // That is the second reason to end the descent high, and it is the one that matters more.
            // Every metre of ramp is a metre the corniche behind it has to stand further back from its
            // own water — the deck's middle is where the channel is centred, and the structure's own
            // half-length already spends 625 m of that budget. The kilometre of ramp this replaces put
            // the coast road half a kilometre from the sea.
            builder.Turn(600f, 14f, 1.6f);
            builder.Straight(240f, RampGrade);

            // --- Onto the axis of the crossing, and level from here to the far anchorage.
            builder.Turn(240f, 74f, 1.4f);

            CrossingStart = builder.Distance;
            builder.Straight(StructureLength, 0f);
            builder.AddFeature(
                RoadFeatureKind.Suspension, CrossingStart, builder.Distance, BridgeName);

            // --- The Anadolu side. Off the deck and down to the shore road.
            builder.Straight(200f, -2.4f);
            builder.Turn(300f, 34f, -3.4f);
            builder.Straight(240f, -3.4f);

            // Looking back at the thing you have just driven over, which is the only place in the world
            // it can be seen end to end.
            builder.AddViewpoint("Köprü Bakışı");

            builder.Turn(380f, -40f, -2.6f);
            builder.Straight(200f, -1.8f);
            builder.AddFuelStation("Yalıköy Benzinlik", 1f);
            builder.Straight(220f, -1.8f);
            builder.Turn(320f, 30f, -1.2f);
            builder.Straight(300f, -0.8f);
        }
    }
}
