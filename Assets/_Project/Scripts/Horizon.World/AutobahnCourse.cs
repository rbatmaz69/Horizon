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
        /// <para>27 m puts the link's shoulder just <i>touching</i> the carriageway's, overlapping by
        /// 0.25 m — the same <c>RibbonOverlap</c> the town's junction pads use where two paved surfaces
        /// meet, and for the same reason: abutting exactly leaves a seam a raycast wheel can drop
        /// through, while overlapping properly puts one mesh collider inside another.</para>
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
        private const float MergeOffset = -27f;

        /// <summary>Straight track before a tunnel portal, so the portal never sits in a curve.</summary>
        private const float PortalApproach = 90f;

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
            // Level because of the terrain rather than because of how a slip road drives. For its whole
            // length this runs 27 m from a road it is not yet clear of, and the ground between two roads
            // that close is a blend of both their heights — so any climb here is a climb of the
            // motorway's own verge with it. At 3 % over 130 m it stood 0.6 m proud of the carriageway.
            // The climb belongs in the curve, once there is distance to absorb it.
            builder.Straight(150f, 0f);

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
