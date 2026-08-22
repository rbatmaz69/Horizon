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

        /// <summary>Where the pass proper begins — the course's start point before the town was given room.</summary>
        private static readonly Vector3 PassStart = new Vector3(0f, 0f, -260f);

        /// <summary>End point of the arrival road, walked from the origin facing +Z.</summary>
        private static readonly Vector3 ArrivalOffset;

        /// <summary>Net heading change over the arrival road, degrees.</summary>
        private static readonly float ArrivalTurn;

        private static readonly float ArrivalSpan;

        static MountainPassCourse()
        {
            var probe = new RoadCourseBuilder(Vector3.zero);
            AppendArrival(probe);

            RoadCourse walked = probe.Build();
            ArrivalOffset = walked.ControlPoints[walked.ControlPoints.Count - 1];
            ArrivalTurn = probe.HeadingDegrees;
            ArrivalSpan = walked.PlannedLength;

            // And the far end, measured the same way the motorway measures its own. The pass used to
            // be the one road in the world that simply stopped; EbentalCourse is grafted onto this
            // point, and it has to follow the run-out whenever the descent is retuned rather than
            // being typed beside it.
            var full = new RoadCourseBuilder(StartPoint, StartHeading);
            Append(full);

            RoadCourse course = full.Build();
            EndPoint = course.ControlPoints[course.ControlPoints.Count - 1];
            EndHeading = full.HeadingDegrees;
        }

        /// <summary>
        /// Where the pass runs out, at the foot of the descent.
        ///
        /// <para>Public for the same reason <see cref="StartPoint"/> is: something is grafted onto it.
        /// See <see cref="EbentalCourse"/>.</para>
        /// </summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there, facing on down the valley. 0 faces +Z, increasing turns towards +X.</summary>
        public static float EndHeading { get; }

        /// <summary>
        /// Length of the arrival road prepended to the pass, metres. Everything downstream of it — the
        /// town, the spawn point, every feature — is measured from here rather than from a literal, so
        /// reshaping the arrival moves them all together.
        /// </summary>
        public static float ApproachLength => ArrivalSpan;

        /// <summary>
        /// Where the course begins, and which way it faces there — the same solve <see cref="Build"/>
        /// does, exposed so nothing else has to keep a copy of the answer.
        ///
        /// <para>Exists for <see cref="AutobahnCourse"/>, which anchors its link road to this point.
        /// Writing the coordinates into that file instead would be two places holding one number, and
        /// the number moves whenever <see cref="AppendArrival"/> is retuned — which is the whole
        /// purpose of that method being a table.</para>
        /// </summary>
        public static Vector3 StartPoint =>
            PassStart - Quaternion.Euler(0f, -ArrivalTurn, 0f) * ArrivalOffset;

        /// <summary>Heading at <see cref="StartPoint"/>. 0 faces +Z, increasing turns towards +X.</summary>
        public static float StartHeading => -ArrivalTurn;

        /// <summary>
        /// Length of the pass's own first straight, and therefore how far past the arrival road the town
        /// may reach.
        ///
        /// <para>Not an aesthetic choice. The town is laid out in coordinates measured along and across
        /// the trunk road, and that mapping folds where the road curves <i>towards</i> the town more
        /// tightly than the town is wide: at 260 m out, the pass's 220 m left-hander squeezes the far
        /// edge of the basin through the centre of its own arc. The town therefore stops exactly where
        /// that turn begins. <c>ValidateTownMapping</c> measures it rather than trusting this comment.</para>
        /// </summary>
        private const float PassFirstStraight = 160f;

        /// <summary>Where the town begins along the course, metres.</summary>
        public static float TownStartDistance => ApproachLength - 340f;

        /// <summary>Where it ends — at the foot of the pass's first bend. See <see cref="PassFirstStraight"/>.</summary>
        public static float TownEndDistance => ApproachLength + PassFirstStraight;

        /// <summary>
        /// The arrival road: eight hundred metres of sub-1 % valley floor before the town.
        ///
        /// Shaped rather than straight, so the town does not sit on a ruler and so you see it from a
        /// distance before you are in it — which is most of what makes a place read as somewhere you
        /// arrive at rather than somewhere that begins at the edge of the screen.
        ///
        /// <para>The two real bends are both finished before the town starts, and the one that runs
        /// through it sweeps the other way at a 650 m radius. That is the town-local mapping again: a
        /// bend away from the town spreads its coordinates apart, which is harmless, while a bend towards
        /// it crushes them together. The rule is one line long and the validator enforces it, but the
        /// shape of this road is where it is obeyed.</para>
        /// </summary>
        private static void AppendArrival(RoadCourseBuilder builder)
        {
            builder.Straight(150f, 0.6f);
            builder.Turn(300f, 20f, 0.6f);
            builder.Straight(80f, 0.8f);
            builder.Turn(260f, -24f, 0.8f);
            builder.Straight(80f, 0.9f);
            builder.Turn(650f, 14f, 1.0f);
            builder.Straight(137f, 1.0f);
        }

        /// <summary>
        /// Builds the pass. Roughly 5.9 km with about 190 m of elevation, climbing anticlockwise around
        /// the mountain — the legs are swept in one consistent direction so the switchback stack rotates
        /// around the summit instead of piling up in a flat plane.
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(StartPoint, StartHeading);
            Append(builder);
            return builder.Build();
        }

        /// <summary>
        /// The shape itself, so the probe in the static constructor and the real walk cannot drift
        /// apart. Two copies of a road are two roads.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // The arrival road is grafted on in front of the pass rather than merged into it: it is
            // started from whatever position and heading puts its *end* exactly on the old start point,
            // facing the way that point used to face. The five kilometres below are therefore unchanged
            // to the millimetre — same switchbacks, same tunnel, same summit, same absolute elevations —
            // and the only thing that moves is where along the course each of them falls, by
            // ApproachLength. Re-deriving a hand-tuned switchback stack to make room for a town would be
            // a poor trade for a rigid transform.
            AppendArrival(builder);

            // --- Valley approach, and the town that sits on it. This and the run-out are the only two
            // stretches of the whole course under 3 %, so it is here or nowhere. The car spawns among the
            // houses and climbs out of them.
            builder.Straight(PassFirstStraight, 1f);
            builder.Turn(220f, -28f, 1f);
            // Split 60 + 60 only so the filling station lands in the middle of this straight rather
            // than on the corner at the end of it. The control points are unchanged: Straight emits one
            // every 10 m and both halves divide by 10, so the road is the same road.
            builder.Straight(60f, 1.5f);

            // The last fuel before the pass, which is a thing every real mountain road has and for the
            // same reason: above this point are thirty switchbacks and no flat ground to build on. Past
            // the last of Talheim's houses and at the foot of the climb, at 1.5 % and dead straight —
            // which is as good as this course gets below the summit.
            builder.AddFuelStation("Tankstelle Talheim", 1f);
            builder.Straight(60f, 1.5f);

            builder.AddFeature(RoadFeatureKind.Village, TownStartDistance, TownEndDistance, "Talheim");

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

            // Split 80 + 80 to sit the station in the middle of the last straight rather than on either
            // end of it. Both halves divide by the 10 m point spacing, so the road is unchanged.
            builder.Straight(80f, -1.5f);

            // The pass's second station, and it is down here rather than at the summit for a reason
            // worth writing down, because the summit is the obvious place and it cannot work.
            //
            // A forecourt has to be levelled, and MountainField levels by planting samples that behave
            // like road samples — so each one forms a shelf a whole verge width around itself, about
            // twenty-four metres. On a switchback stack that reaches the leg below: a platform at the
            // summit dropped 20 m of ground onto the carriageway at 3312 m and walled the road off.
            // Shrinking the pad does not help, because the reach is the verge and not the pad.
            //
            // The run-out is the only other place on this road with both a straight and open ground
            // beside it — the mountain has finished by here, so there is no leg underneath to bury.
            builder.AddFuelStation("Tankstelle Passfuß", 1f);
            builder.Straight(80f, -1.5f);
        }
    }
}
