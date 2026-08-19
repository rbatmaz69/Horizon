using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The road off the west end of the motorway, down to the water and into Seeburg.
    ///
    /// <para><b>West, because that end of the world is empty and low.</b> The motorway's western tip has
    /// no buildings on it, no traffic worth the name and the lowest ground anywhere in the build — the
    /// terrain is already at −56 to −60 m out there. The east end is the other candidate and is cheaper
    /// in tiles, but it would put a coast against Hochstadt's levelled basin, which means a wall of
    /// cliff, and it would hang a sea off the one part of the world where the frame time is already
    /// worst.</para>
    ///
    /// <para><b>A continuation, not a slip road, and that is a departure worth stating.</b> The obvious
    /// join is a mouth built by <c>MotorwayMergeBuilder</c>, which is what the link road uses. It does
    /// not fit: that builder makes an <i>acceleration</i> lane onto a carriageway, and what is wanted
    /// here is the opposite — the motorway running out and a smaller road carrying on where it stopped.
    /// That is exactly what the arterial does at the other end, so this is built the same way, off
    /// <c>WestEndPoint</c> and <c>WestEndHeading</c>.</para>
    ///
    /// <para>It bends twice on the way down. A dead straight run to a beach reads as a slipway; two
    /// gentle turns of a few hundred metres' radius mean the water arrives in the windscreen rather than
    /// having been there the whole way.</para>
    ///
    /// <para><b>It ends at a junction now, not on a beach.</b> The last sixty metres used to be a
    /// parking apron where the road stopped being a road, because there was nothing at the water to
    /// arrive at. There is now: the run-in ends on Seeburg's waterfront boulevard, and
    /// <see cref="EndPoint"/> is what <see cref="SeeburgCourse"/> hangs the town's axis off. The apron
    /// would have been a stub of tarmac inside the junction throat.</para>
    /// </summary>
    public static class CoastCourse
    {
        /// <summary>
        /// Fall along the road, percent.
        ///
        /// <para>Gentle and downhill throughout, so the road meets the water instead of ending above it.
        /// Over the full length it drops about fifteen metres, which is most of the way from the
        /// motorway's −30 m to the −45 the ground is already at out there.</para>
        /// </summary>
        private const float Grade = -1.3f;

        /// <summary>How far the road runs before the first bend, metres.</summary>
        private const float FirstRun = 260f;

        /// <summary>Radius and sweep of the two bends. Open enough to be taken at speed.</summary>
        private const float BendRadius = 320f;

        /// <summary>The run down to the seafront once the bay is in sight.</summary>
        private const float RunIn = 260f;

        /// <summary>
        /// The last stretch, into the junction.
        ///
        /// <para>Nearly level, and it has to be. A road still falling at 1.3% where it meets the
        /// waterfront drags the boulevard's height datum down with it — every floor in the town is
        /// derived from that line by <c>TownShape.FloorHeight</c> — and the sea's surface is derived
        /// from the same place. A ramp here is a town on a slope and a shoreline in the wrong
        /// spot.</para>
        /// </summary>
        private const float ApproachRun = 80f;

        private const float ApproachGrade = -0.3f;

        static CoastCourse()
        {
            // Walked once so the end is measured rather than looked up, the way AutobahnCourse measures
            // its own. SeeburgCourse grafts onto this point, and it has to follow the coast road
            // whenever the bends above are retuned instead of being typed beside it.
            var probe = new RoadCourseBuilder(
                AutobahnCourse.WestEndPoint, AutobahnCourse.WestEndHeading + 180f);

            Append(probe);

            RoadCourse walked = probe.Build();

            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = probe.HeadingDegrees;
        }

        /// <summary>
        /// Where the coast road runs out — on Seeburg's waterfront boulevard, at its harbour.
        ///
        /// <para>Public for the same reason <c>AutobahnCourse.EndPoint</c> is: something is grafted onto
        /// it. See <see cref="SeeburgCourse"/>.</para>
        /// </summary>
        public static Vector3 EndPoint { get; }

        /// <summary>
        /// Heading there, <b>facing the sea</b>. 0 faces +Z, increasing turns towards +X.
        ///
        /// <para>The town's axis crosses this at a right angle, so this direction is also the town's
        /// seaward normal — which is what sites the shoreline, the harbour basin and the moles.</para>
        /// </summary>
        public static float EndHeading { get; }

        /// <summary>
        /// The whole course, from the motorway's west tip to the seafront.
        ///
        /// <para>No probe-and-graft solve, for the same reason the arterial needs none: this starts
        /// where something else finished and runs on, so there is nothing to solve for.</para>
        /// </summary>
        public static RoadCourse Build()
        {
            // Turned about, because WestEndHeading faces the way the traffic arrives — east, into the
            // motorway. Leaving it out is a coast road laid back along the carriageways it came from.
            var builder = new RoadCourseBuilder(
                AutobahnCourse.WestEndPoint, AutobahnCourse.WestEndHeading + 180f);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, so the probe in the static constructor and the real walk cannot drift
        /// apart. Two copies of a road are two roads.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            builder.Straight(FirstRun, Grade);
            builder.Turn(BendRadius, -34f, Grade);
            builder.Straight(300f, Grade);
            builder.Turn(BendRadius, 44f, Grade);

            // Where the bay opens up: the second bend finishes pointing at the water, and this is the
            // last place on the road you can stop and see the whole of it before the town closes in.
            builder.AddViewpoint("Seeburger Bucht");

            builder.Straight(RunIn, Grade);
            builder.Straight(ApproachRun, ApproachGrade);
        }
    }
}
