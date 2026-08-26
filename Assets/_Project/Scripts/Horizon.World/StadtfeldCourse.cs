using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The Stadtfeldstraße: three and a half kilometres of rolling country between Hochstadt's far
    /// edge and the Ebental, and the road that turns the world from a line into a ring.
    ///
    /// <para><b>What it is for.</b> Until this road every course in the world was grafted onto the end
    /// of the one before it and then stopped. Thirty kilometres of tarmac held exactly one fork — the
    /// motorway interchange — so the only decision a driver ever made was whether to turn round. Both
    /// ends of this gap were already dead: Hochstadt's arterial ran 1300 m into the city and expired,
    /// and <see cref="EbentalCourse"/>'s own remarks recorded 2.4 km of empty ground to the south-east
    /// and called it the obvious next leg. It closes both, and the whole eastern half of the world —
    /// the Kalkgrat, the Meerenge, Yalıköy — becomes a branch off the loop rather than the far end of
    /// a corridor.</para>
    ///
    /// <para><b>The character is vertical, because nothing else here is.</b> Every road in this world
    /// is defined by its radii and each is the opposite of its neighbour: the pass lives on 20 m
    /// hairpins at 9.5 %, the Ebental on nothing under 150 m and nothing over 3 %, the Kalkgrat on
    /// 240–450 m sweepers at 6.6 % and then 38–70 m corners at −8.4 %, the motorway on nothing under
    /// 850 m. What no road has used is the profile. The Ebental has a single crest and its own file
    /// calls it "the one genuinely demanding moment on an otherwise open road"; this is that idea for
    /// three and a half kilometres. The corners are ordinary — 260 to 320 m, third and fourth gear —
    /// and the interest is that the road climbs 25 m out of the city, drops 13 into a hollow, climbs
    /// 12, drops 10, and climbs 15 again, so a corner exit is regularly over a rise rather than in
    /// front of you.</para>
    ///
    /// <para>That also happens to be the only character that survives repetition, which this road
    /// needs more than any other: it is the closing side of a ring and will be driven in both
    /// directions more often than anything except the pass.</para>
    ///
    /// <para><b>No set-piece, deliberately.</b> The obvious idea is the city appearing from the first
    /// crest, which stands 25 m over Hochstadt's floor. It cannot work and the project has already
    /// paid for finding that out once, at the Kalkgrattunnel: the camera's far plane is 600 m with the
    /// fog wall inside it, so nothing in this world is revealed from further than about half a
    /// kilometre. The city arrives in the last few hundred metres or not at all, and every rise here
    /// shows the next dip rather than the horizon.</para>
    ///
    /// <para><b>It starts at the boulevard's last node, not at the arterial's end, and that is not a
    /// detail.</b> <see cref="HochstadtCourse"/> is never paved — <c>PrototypeSetup</c> builds it as
    /// <c>ArterialPath</c> and hands it to <c>PrepareTown</c>, because a town's trunk road only has to
    /// be a coordinate frame and a height datum. The road a driver is on through Hochstadt is the
    /// boulevard in <c>HochstadtLayout</c>, which ends 120 m short of the arterial does, and those
    /// 120 m are bare datum for the town's skirt rings. Grafted onto the arterial's end this road
    /// would have started in a field with a gap between it and the city — which is why it is grafted
    /// onto <see cref="HochstadtCourse.EastGatePoint"/> instead, and why its first instruction carries
    /// the arterial's own grade rather than a grade of its own.</para>
    ///
    /// <para><b>Both ends are pinned, which is what <c>RoadCourseBuilder.ConnectTo</c> exists for.</b>
    /// The start is ordinary probe-and-graft — the boulevard hands over and this carries on, so there
    /// is no junction at that end at all and nothing to solve. The far end has to arrive at a pose
    /// <see cref="EbentalCourse"/> decides, and the last 359 m are therefore a Dubins solve rather
    /// than a hand-tuned table that would need re-tuning every time either road moved. See
    /// <see cref="ConnectLimit"/> for the one way that solve goes wrong quietly.</para>
    /// </summary>
    public static class StadtfeldCourse
    {
        /// <summary>The road's name, for the map and for anything that reports a course.</summary>
        public const string RoadName = "Stadtfeldstraße";

        /// <summary>
        /// How far the branch is turned off the Ebental where it leaves it, degrees. Positive is a
        /// right-hander, which is the side Hochstadt is on.
        ///
        /// <para><b>Sized from the ground rather than from the drawing.</b> Straight on from the fork
        /// the Ebental runs 200 m and the Kalkgrat another 400 before it turns, so two roads leaving
        /// at an angle θ are 2·sin(θ/2) metres apart per metre travelled over that stretch.
        /// <c>MountainField</c> plants a shelf 80 m wide around every road and its coarse grid reaches
        /// 250 m, so the branch has to be a clear 250 m from the trunk before either of them starts
        /// deciding the other's ground: that wants at least 29°. It is also the smallest angle that
        /// still reads as a fork — the bearing to Hochstadt is only 20° off the Ebental's own heading,
        /// so at the honest angle this junction would look like a road that simply widens.</para>
        /// </summary>
        public const float ForkDeflection = 32f;

        /// <summary>
        /// Radius of both corners of the closing solve, metres. In the same band as the authored
        /// corners above it, so the join does not announce itself.
        /// </summary>
        private const float ConnectRadius = 300f;

        /// <summary>
        /// How long the closing solve is allowed to be, metres — and this is a guard rather than a
        /// tuning knob.
        ///
        /// <para><b><c>ConnectTo</c> has one failure mode that reports success.</b> It takes the
        /// shortest of four Dubins families, and <c>TurnBy</c> deliberately goes the long way round
        /// rather than crossing zero, because the circle a corner is on only turns one way. When the
        /// authored road above ends in a pose the target does not suit, the shortest family that
        /// exists can be one that turns through 300° — a full loop of carriageway in the middle of a
        /// country road, geometrically exact, arriving on the target to the millimetre, and logging
        /// nothing. It is 359 m here; at four degrees more or less on the corner out of the city it is
        /// 2200, and no radius between 220 and 380 m rescues it. Two kilometres of road that nobody
        /// asked for builds and validates cleanly; only a picture or this number catches it.</para>
        ///
        /// <para>The table below is why the corner out of the city is 122° and not a rounder number.
        /// Its plateau is four degrees wide and flat across every radius, so the instruction sits in
        /// the middle of it rather than on an edge:</para>
        ///
        /// <code>
        ///   corner    220 m   260 m   300 m   340 m   380 m
        ///     118°     LOOP    LOOP    LOOP    LOOP    LOOP
        ///     120°      343     343     343     343     343
        ///     122°      359     359     359     359     359
        ///     123°      377     377     377     378     378
        ///     124°      401     403     405    LOOP    LOOP
        /// </code>
        /// </summary>
        private const float ConnectLimit = 700f;

        /// <summary>
        /// Which way the road faces where it meets the Ebental — the branch's own heading at the fork,
        /// reversed, because this course is walked from the city towards it.
        /// </summary>
        private static float ArrivalHeading =>
            EbentalCourse.ForkHeading + ForkDeflection + 180f;

        /// <summary>
        /// The whole course, from where Hochstadt's arterial runs out to the fork on the Ebental.
        ///
        /// <para>No probe-and-graft solve, for the reason <see cref="HochstadtCourse"/> needs none:
        /// this starts where something else finished and runs on. What is unusual is the other end —
        /// see the class remarks.</para>
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(
                HochstadtCourse.EastGatePoint, HochstadtCourse.EastGateHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself. Kept apart from <see cref="Build"/> to match every other course in the
        /// project, where a probe walk and the real one share it so the two cannot drift apart.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- Out of the city, on the arterial's own line and at the arterial's own grade.
            //
            // The grade is HochstadtCourse.Grade rather than a number of this road's own, and it is the
            // rule AutobahnCourse.MotorwayGradeAtJunction states: two ribbons that touch have to agree
            // about their grade or the join is a step. The boulevard's last node takes its height from
            // the arterial, so anything else here is a lip at the city gate.
            //
            // 180 m of it, which carries the road past the 120 m of datum tail the town's skirt rings
            // sit against before it starts turning. Turning inside that would put a climbing road
            // through ground that is levelled to the town.
            builder.Straight(180f, HochstadtCourse.Grade);

            // The turn off the city's axis and onto the country line, and the tightest corner on the
            // road.
            //
            // 260 m and not more: a left-hander from this heading swings east before it comes back
            // west, and the wider it is the further that excursion reaches back towards Hochstadt's
            // north-east corner. At 260 the road's closest approach to the town's footprint past the
            // gate is 251 m, which is exactly MountainField's coarse reach — any wider and a road
            // climbing at 2.4 % starts deciding the height of ground that is levelled to a town floor.
            //
            // 122° and not a rounder number for a different reason entirely, and one worth reading
            // ConnectLimit for before touching this line.
            builder.Turn(260f, -122f, ClimbGrade);

            // --- The climb out. The one genuinely long pull on the road, and the first crest is at
            // the top of it, 25 m above the city.
            builder.Straight(300f, 3.0f);
            builder.Turn(300f, 26f, 1.6f);

            // --- First hollow. The corner is at the bottom of it, which is the point: a corner in a
            // dip is one you can see all of, and the corner after the next crest is one you cannot.
            builder.Straight(240f, -2.8f);
            builder.Turn(260f, -44f, FallGrade);

            // --- Second crest, and the corner climbs over it rather than stopping at the foot.
            builder.Straight(280f, 2.2f);
            builder.Turn(320f, 30f, 3.2f);

            // --- Second hollow.
            builder.Straight(220f, -2.6f);
            builder.Turn(270f, -34f, -2.8f);

            // --- Up to the last crest, which is 359 m short of the fork and three metres above it.
            // The junction is therefore in sight for the whole approach rather than arriving over a
            // rise, which is the one place on this road where hiding what is ahead would be a fault
            // rather than the feature.
            builder.Straight(260f, 2.4f);
            builder.Turn(300f, 40f, 2.0f);
            builder.Straight(400f, 1.2f);

            // --- And down onto the Ebental. See ConnectLimit for what is being checked here and why
            // the build cannot be trusted to notice on its own.
            float before = builder.Distance;

            builder.ConnectTo(EbentalCourse.ForkPoint, ArrivalHeading, ConnectRadius);

            float connected = builder.Distance - before;

            if (connected > ConnectLimit)
            {
                Debug.LogError(
                    $"[Horizon] The Stadtfeldstraße's closing solve came out {connected:0} m long "
                    + $"against a {ConnectLimit:0} m limit. ConnectTo takes the shortest Dubins family "
                    + "that exists and one of them loops the long way round, so this is a road with a "
                    + "circle in it rather than a road that failed to build. Retune the instructions "
                    + "above so the walk ends nearer the fork and closer to its heading.");
            }

            // The branch's own copy of the fork. The Ebental carries one too, and both are needed:
            // GuardRailBuilder, DelineatorPostBuilder and KerbBuilder each read IsJunction off the
            // course they are building, so a mark on one road protects one road. This end of this road
            // is the mouth, which is exactly where the drop test beside a junction fires and where a
            // rail would stand across the Ebental.
            builder.AddJunction(EbentalCourse.JunctionName);
        }

        /// <summary>
        /// Grade of the pull out of the city, percent. Steeper than anything on the Ebental and less
        /// than half the Kalkgrat's climb, which is the band this road lives in.
        /// </summary>
        private const float ClimbGrade = 2.4f;

        /// <summary>Grade into the deeper of the two hollows. See <see cref="ClimbGrade"/>.</summary>
        private const float FallGrade = -3.0f;
    }
}
