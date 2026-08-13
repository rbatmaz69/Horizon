using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The motorway that runs across the valley mouth, and the link road that joins it to the pass.
    ///
    /// <para>The tuning table for the second road in the world, and a sibling of
    /// <see cref="MountainPassCourse"/> in every sense — same builder, same convention that everything
    /// here is a number to be argued with. What it is <i>not</i> is a second copy of that file's ideas:
    /// where the pass is authored around hairpins and grade, this is authored around radius and sight
    /// line. Nothing here turns tighter than 850 m, and the steepest grade is half a percent.</para>
    ///
    /// <para><b>One centreline, two carriageways.</b> This course is never paved. It is the median line,
    /// and the two carriageways are <see cref="OffsetRoadPath"/>s at ±<see cref="CarriagewayOffset"/>
    /// either side of it. That is why the tunnels and bridges below are recorded once, in centreline
    /// distances: the offset paths share this parameterisation, so a portal or a span lands square on
    /// both sides without being described twice.</para>
    /// </summary>
    public static class AutobahnCourse
    {
        /// <summary>
        /// Distance from the median line to the centre of each carriageway, metres.
        ///
        /// <para>7.5 m of carriageway half-width plus a 3 m median, so the two inner hard shoulders are
        /// 6 m apart with the barrier down the middle of that gap. Widening this widens the median and
        /// nothing else — the carriageways themselves come from
        /// <see cref="RoadShape.Autobahn"/>.</para>
        /// </summary>
        public const float CarriagewayOffset = 10.5f;

        /// <summary>
        /// How far out from the median the link road runs where it meets the motorway.
        ///
        /// <para>25.75 m overlaps the carriageway's shoulder by 1.5 m. It was 27, which overlapped by
        /// the town's <c>RibbonOverlap</c> of 0.25 — enough to stop a raycast wheel dropping through the
        /// seam, which was all it was chosen for, and not enough to <i>read</i> as a join. A slip road
        /// that merely touches the road it feeds looks like a slip road that stops next to it.</para>
        ///
        /// <para>It was 15 m, which laid the link's ribbon straight over the outer half of the
        /// carriageway. That reads as a merge in a screenshot and is two mesh colliders a few
        /// centimetres apart in the physics scene — the driveable-corridor check found it at 106 points.
        /// Running alongside instead means driving off the link crosses two shoulders to reach the
        /// carriageway, which is what a slip road is.</para>
        ///
        /// <para><b>Negative, and that sign is load-bearing.</b> It puts the link on the same side of
        /// the motorway as the valley it climbs to. Positive put it on the far side, so the loop had to
        /// cross back over the carriageways to reach the pass — and it crossed them several metres
        /// higher, which the terrain resolves by averaging the two roads it can see. The result was a
        /// five-metre ridge standing through the westbound carriageway. A road that has to get to the
        /// other side of a motorway needs a bridge, not a curve.</para>
        ///
        /// <para><b>Still a deliberate simplification.</b> A real interchange has a separate slip road
        /// per direction with a proper taper and a nose; this is one two-way link laid against the
        /// nearside carriageway. <c>StreetJunctionBuilder.AppendTrunkMouth</c> is the nearest existing
        /// model for building it properly.</para>
        /// </summary>
        private const float MergeOffset = -25.75f;

        /// <summary>Straight track before a tunnel portal, so the portal never sits in a curve.</summary>
        private const float PortalApproach = 90f;

        /// <summary>
        /// The motorway's grade where the link leaves it, percent.
        ///
        /// <para>Must match the first instruction of <see cref="AppendEastLeg"/>. The two are the same
        /// number written twice, which is a thing to keep in step — but the alternative is the link
        /// interrogating a course it is part of building, and a 0.45 m step between two touching ribbons
        /// is easier to see in a diff than in a constructor.</para>
        /// </summary>
        private const float MotorwayGradeAtJunction = 0.3f;

        private static readonly Vector3 LinkOffset;
        private static readonly float LinkTurn;
        private static readonly Vector3 WestOffset;
        private static readonly float WestTurn;

        static AutobahnCourse()
        {
            // The same probe-and-graft solve MountainPassCourse uses for its arrival road, applied
            // twice. Each stretch is walked once from the origin purely to measure where it ends up and
            // which way it is facing by then; the real walk is then started from whatever position and
            // heading puts that end exactly where it has to be. It is the only way to graft one road
            // onto another without re-deriving a tuned shape by hand every time either moves.
            var linkProbe = new RoadCourseBuilder(Vector3.zero);
            AppendLink(linkProbe);
            RoadCourse walkedLink = linkProbe.Build();
            LinkOffset = walkedLink.ControlPoints[walkedLink.ControlPoints.Count - 1];
            LinkTurn = linkProbe.HeadingDegrees;
            LinkSpan = walkedLink.PlannedLength;

            // The link is authored motorway-first and ends on the pass, so its start heading is whatever
            // leaves it pointing the way the pass begins.
            LinkStartHeading = MountainPassCourse.StartHeading - LinkTurn;
            LinkStart = MountainPassCourse.StartPoint
                        - Quaternion.Euler(0f, LinkStartHeading, 0f) * LinkOffset;

            // The median line runs MergeOffset to the left of where the link starts, because the link
            // starts on the carriageway rather than on the middle of the road.
            Vector3 right = Quaternion.Euler(0f, LinkStartHeading, 0f) * Vector3.right;
            JunctionPoint = LinkStart - right * MergeOffset;

            var westProbe = new RoadCourseBuilder(Vector3.zero);
            AppendWestLeg(westProbe);
            RoadCourse walkedWest = westProbe.Build();
            WestOffset = walkedWest.ControlPoints[walkedWest.ControlPoints.Count - 1];
            WestTurn = westProbe.HeadingDegrees;

            // The motorway is parallel to the link where they meet, so it passes the junction on the
            // link's start heading.
            MainStartHeading = LinkStartHeading - WestTurn;
            MainStart = JunctionPoint - Quaternion.Euler(0f, MainStartHeading, 0f) * WestOffset;
            JunctionDistance = walkedWest.PlannedLength;

            // Where the road runs out, measured rather than looked up, so whatever HochstadtCourse
            // grafts onto it stays attached when either leg is retuned.
            var full = new RoadCourseBuilder(MainStart, MainStartHeading);
            AppendWestLeg(full);
            AppendEastLeg(full);

            RoadCourse walked = full.Build();
            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = full.HeadingDegrees;
        }

        /// <summary>The far end of the motorway, on the median line — where the city begins.</summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there. 0 faces +Z, increasing turns towards +X.</summary>
        public static float EndHeading { get; }

        /// <summary>Where the link road meets the motorway, on the median line.</summary>
        public static Vector3 JunctionPoint { get; }

        /// <summary>How far along the motorway that junction falls, metres.</summary>
        public static float JunctionDistance { get; }

        /// <summary>Start of the link road — beside the motorway, not on its centreline.</summary>
        public static Vector3 LinkStart { get; }

        /// <summary>Heading of both the link and the motorway at the junction.</summary>
        public static float LinkStartHeading { get; }

        /// <summary>Length of the link road, metres.</summary>
        public static float LinkSpan { get; }

        /// <summary>
        /// Where the motorway begins, on the median line — the western edge of the built world.
        ///
        /// <para>Public for the same reason <see cref="EndPoint"/> is: something is grafted onto it.
        /// The city hangs off the east end and the coast road off this one, and both have to follow
        /// their end of the motorway whenever it is retuned rather than being typed beside it.</para>
        /// </summary>
        public static Vector3 WestEndPoint => MainStart;

        /// <summary>Heading there, <b>facing east</b> — the way the traffic arrives. See
        /// <see cref="WestEndPoint"/>.</summary>
        public static float WestEndHeading => MainStartHeading;

        private static Vector3 MainStart { get; }

        private static float MainStartHeading { get; }

        /// <summary>
        /// The motorway's median line, about 8.5 km with two tunnels and two viaducts.
        ///
        /// <para>Laid out around the junction rather than from one end: the west leg is walked to reach
        /// it and the east leg away from it, so retuning either one leaves the other and the link
        /// exactly where they were.</para>
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(MainStart, MainStartHeading);

            AppendWestLeg(builder);
            AppendEastLeg(builder);

            return builder.Build();
        }

        /// <summary>The two-way link between the motorway and the foot of the pass.</summary>
        public static RoadCourse BuildLink()
        {
            var builder = new RoadCourseBuilder(LinkStart, LinkStartHeading);
            AppendLink(builder);
            return builder.Build();
        }

        /// <summary>
        /// Motorway to valley floor: a taper alongside the carriageway, a long loop round, and a run-in.
        ///
        /// <para>The loop is 220 m and turns a hundred degrees, which is a slip road rather than a
        /// motorway curve and is meant to be — it is the one place on this road you have to slow down
        /// for, and it is what separates the two speeds either side of it. It climbs 18.5 m in total,
        /// which is what puts the motorway below the valley floor.</para>
        /// </summary>
        private static void AppendLink(RoadCourseBuilder builder)
        {
            // The taper: straight, parallel to the carriageway it is leaving, and <b>level</b>.
            //
            // On the motorway's own grade, not level and not the climb.
            //
            // Level was wrong in a way that is obvious once seen: the motorway rises at 0.3 % through
            // the interchange and a level taper beside it is 0.45 m below the carriageway by the end of
            // its 150 m. The two ribbons stopped touching, and a slip road that ends in a step is not
            // connected to anything.
            //
            // It is not the *climb* either, and that is what the level version was protecting. This runs
            // 26 m from a road it is not yet clear of, and the ground between two roads that close is a
            // blend of both their heights — so a real climb here lifts the motorway's own verge with it,
            // and at 3 % it stood 0.6 m proud of the carriageway. Matching the road it is leaving costs
            // 0.45 m over the whole taper, which is a twentieth of that. The climb belongs in the curve,
            // once there is distance to absorb it.
            builder.Straight(150f, MotorwayGradeAtJunction);

            builder.Turn(220f, -100f, 3.6f);
            builder.Straight(180f, 2.6f);
        }

        /// <summary>
        /// West of the junction, walked towards it. Tunnel through a spur, viaduct over a side valley.
        /// </summary>
        private static void AppendWestLeg(RoadCourseBuilder builder)
        {
            builder.Straight(420f, -0.3f);
            builder.Turn(950f, 24f, -0.4f);
            builder.Straight(520f, 0.3f);

            // Portals on straight track, for the same reason the pass puts them there: TunnelBuilder
            // sweeps a massif well past each portal, and through a curve that body folds through itself.
            builder.Straight(PortalApproach, 0.2f);
            float tunnelStart = builder.Distance;
            builder.Straight(260f, 0.2f);
            builder.AddFeature(RoadFeatureKind.Tunnel, tunnelStart, builder.Distance, "Steinbergtunnel");
            builder.Straight(PortalApproach, 0.2f);

            builder.Turn(1200f, -20f, 0.5f);
            builder.Straight(640f, 0.4f);

            // Level across the span. A bridge is built flat here not because real ones are, but because
            // the deck, the piers and the parapet are all measured off the carriageway, and a grade
            // across the span is one more thing to be wrong about before the first one is right.
            float bridgeStart = builder.Distance;
            builder.Straight(280f, 0f);
            builder.AddFeature(RoadFeatureKind.Bridge, bridgeStart, builder.Distance, "Wiesentalbrücke");

            builder.Straight(360f, -0.3f);
            builder.Turn(850f, 16f, 0f);
            builder.Straight(480f, 0.2f);
        }

        /// <summary>East of the junction, walked away from it. The longer viaduct is on this side.</summary>
        private static void AppendEastLeg(RoadCourseBuilder builder)
        {
            builder.Straight(500f, 0.3f);
            builder.Turn(1000f, -22f, 0.4f);
            builder.Straight(560f, 0.5f);

            float bridgeStart = builder.Distance;
            builder.Straight(320f, 0f);
            builder.AddFeature(RoadFeatureKind.Bridge, bridgeStart, builder.Distance, "Talbrücke Hochfeld");

            builder.Straight(420f, 0.2f);
            builder.Turn(1150f, 18f, -0.3f);
            builder.Straight(600f, -0.4f);

            builder.Straight(PortalApproach, -0.2f);
            float tunnelStart = builder.Distance;
            builder.Straight(240f, -0.2f);
            builder.AddFeature(RoadFeatureKind.Tunnel, tunnelStart, builder.Distance, "Ohlenbergtunnel");
            builder.Straight(PortalApproach, -0.2f);

            builder.Turn(900f, -15f, -0.2f);
            builder.Straight(520f, -0.3f);
        }
    }
}
