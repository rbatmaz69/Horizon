using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The road off the west end of the motorway, down to the water.
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

        /// <summary>The parking apron at the end, where the road stops being a road.</summary>
        public const float ApronLength = 60f;

        /// <summary>
        /// The whole course, from the motorway's west tip to the water's edge.
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

            builder.Straight(FirstRun, Grade);
            builder.Turn(BendRadius, -34f, Grade);
            builder.Straight(300f, Grade);
            builder.Turn(BendRadius, 44f, Grade);
            builder.Straight(280f, Grade);

            // The last stretch flattens out. A ramp that is still falling at the waterline carries on
            // falling under it, and what should be a beach becomes a boat slip.
            builder.Straight(ApronLength, -0.3f);

            builder.AddViewpoint("Westmeer");

            return builder.Build();
        }
    }
}
