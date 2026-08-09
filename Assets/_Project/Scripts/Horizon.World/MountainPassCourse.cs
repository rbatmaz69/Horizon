using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The mountain pass itself: valley approach, switchback climb, summit, switchback descent,
    /// run-out into the far valley.
    ///
    /// This is the tuning table for the whole world. Everything here is a number to be argued with —
    /// grades, hairpin radii, how long the legs are — and none of it is structural, which is the point
    /// of driving the layout from <see cref="RoadCourseBuilder"/> rather than from a formula.
    /// </summary>
    public static class MountainPassCourse
    {
        /// <summary>Radius of the climbing hairpins, metres.</summary>
        private const float ClimbHairpinRadius = 20f;

        /// <summary>Radius of the descending hairpins. A little more open, so the way down flows.</summary>
        private const float DescentHairpinRadius = 22f;

        /// <summary>Turn angle of a hairpin. Not a full 180 — the legs fan out as the pass rotates.</summary>
        private const float HairpinAngle = 170f;

        /// <summary>Grade of the legs between hairpins, percent.</summary>
        private const float ClimbGrade = 9.5f;

        private const float DescentGrade = -9.5f;

        /// <summary>
        /// Hairpins flatten off. Real ones do, and taking a 20 m radius at 9.5% is unpleasant in a way
        /// that reads as a bug rather than as a challenge.
        /// </summary>
        private const float HairpinGrade = 4f;

        private const int ClimbHairpins = 7;
        private const int DescentHairpins = 5;

        /// <summary>
        /// How much a leg curves. Alternates in sign with the hairpins so it cancels over each pair —
        /// see the comment at the call site for what happens when it does not.
        /// </summary>
        private const float LegSweep = 14f;

        /// <summary>
        /// Straight track before a tunnel or gallery portal, metres.
        ///
        /// Comfortably more than <c>TunnelBuilder.EndOverhang</c> (24 m), which is how far the rock body
        /// runs past the portal. The exit side needs nothing: a <see cref="RoadCourseBuilder.Leg"/> follows
        /// both structures and opens with about a hundred metres of straight.
        /// </summary>
        private const float PortalApproach = 60f;

        /// <summary>
        /// Builds the pass. Roughly 5.2 km with about 190 m of elevation, climbing anticlockwise around
        /// the mountain — the legs are swept in one consistent direction so the switchback stack rotates
        /// around the summit instead of piling up in a flat plane.
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(new Vector3(0f, 0f, -260f));

            // --- Valley approach. Room to get up to speed before the first corner.
            builder.Straight(160f, 1f);
            builder.Turn(220f, -28f, 1f);
            builder.Straight(120f, 1.5f);

            // --- Climb.
            for (int i = 0; i < ClimbHairpins; i++)
            {
                // A tunnel partway up, where the road would otherwise have to cut through a spur.
                if (i == 3)
                {
                    // A portal cannot sit in the exit of a hairpin. TunnelBuilder carries its massif
                    // EndOverhang (24 m) past each portal, and that cross-section is 104 m across — swept
                    // along a 20 m radius it folds through itself and reaches back over the approach leg.
                    // Real passes put their portals on straight track for the same reason.
                    builder.Straight(PortalApproach, 6f);

                    // 170 m, not 80. A tunnel has to last long enough that the outside is genuinely gone
                    // and you are driving on your own headlights — at 80 m and 100 km/h it is over in
                    // under three seconds, which reads as a covered cutting rather than a bore.
                    float tunnelStart = builder.Distance;
                    builder.Straight(170f, 6f);
                    builder.AddFeature(RoadFeatureKind.Tunnel, tunnelStart, builder.Distance, "Kehrtunnel");
                }

                float direction = (i % 2 == 0) ? 1f : -1f;

                // The sweep must alternate with the hairpins, so that it cancels out over each pair of
                // legs. A sweep that always went the same way accumulated into a heading drift, the legs
                // fanned out, and the road eventually ran back over itself — at one point passing 1.8 m
                // from itself in plan with 156 m between them vertically. The terrain takes its height
                // from the nearest piece of road, so it buried the lower carriageway under the mountain.
                builder.Leg(240f, 150f, LegSweep * direction, ClimbGrade);

                if (i == 4)
                {
                    builder.AddViewpoint("Talblick");
                }

                // Alternating hairpins are what make a switchback rather than a spiral.
                builder.Turn(ClimbHairpinRadius, HairpinAngle * direction, HairpinGrade);
            }

            // --- Summit. Somewhere to stop, which the concept asks for and the view earns.
            builder.Straight(70f, 2.5f);
            builder.AddViewpoint("Passhöhe");
            builder.Straight(110f, 0f);
            builder.Turn(180f, 34f, -1f);

            // --- Descent.
            for (int i = 0; i < DescentHairpins; i++)
            {
                if (i == 2)
                {
                    // Same as the tunnel: the massif must not be swept through the hairpin above it.
                    builder.Straight(PortalApproach, DescentGrade);

                    float galleryStart = builder.Distance;
                    builder.Straight(110f, DescentGrade);
                    builder.AddFeature(RoadFeatureKind.Gallery, galleryStart, builder.Distance, "Felsgalerie");
                }

                float direction = (i % 2 == 0) ? -1f : 1f;

                builder.Leg(250f, 165f, LegSweep * direction, DescentGrade);
                builder.Turn(DescentHairpinRadius, HairpinAngle * direction, HairpinGrade * -1f);
            }

            // --- Run-out into the far valley.
            builder.Straight(140f, -4f);
            builder.Turn(260f, 24f, -2f);
            builder.Straight(160f, -1.5f);

            return builder.Build();
        }
    }
}
