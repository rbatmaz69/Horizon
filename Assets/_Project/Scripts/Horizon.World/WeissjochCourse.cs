using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The road to the Weissjoch: twelve and a half kilometres and nine hundred metres of climb, off
    /// the motorway's western leg and up into the snow. The highest thing in the world by a factor of
    /// four and a half, and the first winter in it.
    ///
    /// <para><b>Why it is this long.</b> The world's steepest legs are the pass's 9.5 %, which
    /// CLAUDE.md says should stay that way, and a hairpin stack gives back about 8.5 % once the
    /// flattened corners are counted. Nine hundred and forty metres of climb at that rate is twelve
    /// kilometres of road. The length is not a taste; it is what the height costs.</para>
    ///
    /// <para><b>The road is the mountain, and that decides the shape of it.</b>
    /// <see cref="MountainField"/> averages road samples inside 250 m, takes the nearest sample's
    /// height verbatim beyond that, and clamps at the edge of its own grid — so there is no ground
    /// anywhere in this world that the roads did not put there. A high pass with long traverses out
    /// into open country does not make a mountain; it makes a plateau in the sky at its own height,
    /// with no valley beneath it and no summit above it. Altitude is only visible where a <i>lower</i>
    /// leg runs within a couple of hundred metres of a higher one. So this climb is one compact stack
    /// and not a tour, and every stage of it is stacked over the one below.</para>
    ///
    /// <para><b>The stack advances north, and the face therefore looks south at the motorway.</b> The
    /// ground is steep only along the direction the stack advances; across it the field clamps to leg
    /// height and goes flat. That grain is the mountain's one free decision, and pointing it at the
    /// road that leads here means the wall is visible from the carriageway — the exit reads as the way
    /// up <i>that</i> rather than as a slip road into fog.</para>
    ///
    /// <para><b>Four stages, and they are four places rather than one corner told twenty-eight
    /// times.</b> The tree line at 460 m falls at the top of stage B and the snow line at 650 m at the
    /// top of stage C, so the road climbs out of forest into rock and out of rock into snow. That is
    /// where the variety comes from. Widening the legs and opening the hairpins as it climbs is the
    /// second axis: the Kalkgrat's remarks record that corner radius sets the plan separation while leg
    /// length sets the rise across it, and that the second is the one that makes a cliff.</para>
    ///
    /// <para><b>It was a dead end, and is not one any more.</b> The col used to be where the road
    /// stopped and the descent was the whole of what was up here. <c>WeissjochringCourse</c> now hangs
    /// off the far side of it, so this course publishes an end pose like every other road here — see
    /// <see cref="EndPoint"/>. The remark that used to stand in this place said it exposed no
    /// <c>EndPoint</c>, which is exactly the sort of doc comment that is worse than none once it stops
    /// being true.</para>
    /// </summary>
    public static class WeissjochCourse
    {
        /// <summary>The col, and the name of everything up here.</summary>
        public const string ColName = "Weissjoch";

        /// <summary>
        /// Where the trees stop, in metres above the world's zero — an <b>absolute</b> elevation, not a
        /// fraction of anything.
        ///
        /// <para>The rest of the world's tree line is <c>VegetationShape.TreeLineHeight</c>, 0.82 of the
        /// span between the mountain pass's lowest point and its summit, which today puts it at 160 m.
        /// That axis cannot be stretched to cover this mountain: doing so would move the tree line to
        /// over seven hundred metres <i>everywhere</i> and wood the pass to its own summit.
        /// <c>VegetationBuilder</c> therefore takes this number when a region carries one and falls back
        /// to the fraction where none does, which leaves every existing road exactly as it was.</para>
        ///
        /// <para><b>700 m, up from 460.</b> The first setting put the band on a stage boundary, which
        /// was tidy and left three quarters of a nine-hundred-metre mountain with nothing growing on it
        /// — the road climbed out of the wood before it was half way up. Real spruce goes far higher
        /// than that, and the interesting country on an alpine pass is the last of the forest rather
        /// than the rock above it. At 700 the trees run to within two hundred metres of the col and,
        /// because <see cref="SnowLineElevation"/> is below it, the top of the wood stands in snow.</para>
        /// </summary>
        public const float TreeLineElevation = 700f;

        /// <summary>
        /// Where the snow starts, absolute metres. See <see cref="TreeLineElevation"/> for why it is
        /// absolute.
        ///
        /// <para><b>600 m, and deliberately a hundred metres <i>below</i> the tree line rather than
        /// above it.</b> The two crossing is the whole picture a winter region is for: a band where dark
        /// spruce stands on white ground, which is worth more than either a bare snowfield or a green
        /// wood and costs nothing that the two lines were not already costing.</para>
        /// </summary>
        public const float SnowLineElevation = 600f;

        /// <summary>
        /// Turn angle of a hairpin, and the number that decides how steep the mountain is.
        ///
        /// <para><b>176 with a 4° leg sweep, so the two sum to 180 and the stack is exact.</b> The pass
        /// uses 170 and 14, which sums to 184 — it over-rotates by four degrees a corner and alternates
        /// so the error cancels over a pair, which is what makes its switchbacks fan around the summit.
        /// Fanning costs advance: measured over this table, 170/14 spreads the stack 2273 m for the same
        /// climb and gives a 39 % face, while 176/4 spreads it 1850 m and gives 48 %. A mountain is the
        /// steeper of those. The sweep is kept rather than dropped to zero because a dead-straight leg
        /// between two hairpins is a ruler, and 4° is enough to see.</para>
        /// </summary>
        private const float HairpinAngle = 176f;

        /// <summary>See <see cref="HairpinAngle"/>. Alternates with the hairpins or the legs fan out.</summary>
        private const float LegSweep = 4f;

        /// <summary>Hairpins flatten off, for the reason the pass's do.</summary>
        private const float HairpinGrade = 4f;

        /// <summary>
        /// Straight track before a portal, metres. Sixty, as on both other pass roads — comfortably
        /// past <c>TunnelBuilder.EndOverhang</c>. The motorway's ninety is a fast road's number.
        /// </summary>
        private const float PortalApproach = 60f;

        /// <summary>
        /// Grade of the last stretch past the col, percent — and the number the road grafted onto it
        /// has to start on.
        ///
        /// <para>Two ribbons that touch have to agree about their grade or the join is a step. This is
        /// the same contract <c>AutobahnCourse.WeissjochGradeAtExit</c> has with the first instruction
        /// of this course, one road further along.</para>
        /// </summary>
        public const float EndGrade = 0.3f;

        /// <summary>
        /// Track past the col before the circuit's access road takes over, metres.
        ///
        /// <para>The col's own furniture — a forecourt and a viewpoint — wants the same flat, level
        /// ground the pad already asked for, and a fork mouth wants straight and level track of its own.
        /// This is the gap between the two, so neither is standing in the other.</para>
        /// </summary>
        private const float ColRunOut = 140f;

        /// <summary>
        /// Walks the shape once as a measurement so the end pose exists before anything is built with
        /// it.
        ///
        /// <para>The probe and the real walk both go through <see cref="Append"/> and nothing else, so
        /// they cannot drift apart. Two copies of a road are two roads.</para>
        /// </summary>
        static WeissjochCourse()
        {
            var probe = new RoadCourseBuilder(
                AutobahnCourse.WeissjochCapPoint, AutobahnCourse.WeissjochCapHeading);

            Append(probe);

            RoadCourse walked = probe.Build();

            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = probe.HeadingDegrees;
        }

        /// <summary>Where the road stops past the col, and where the circuit's access road starts.</summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there, in the builder's convention.</summary>
        public static float EndHeading { get; }

        /// <summary>
        /// The whole road, from the ramp's cap beside the westbound carriageway to the far side of the
        /// col.
        ///
        /// <para>No inverse solve at the near end: this starts where <see cref="AutobahnCourse"/>
        /// publishes the ramp's cap and runs on.</para>
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(
                AutobahnCourse.WeissjochCapPoint, AutobahnCourse.WeissjochCapHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, kept apart from <see cref="Build"/> to match every other course here.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- Off the motorway. The taper runs parallel to the carriageway at the carriageway's own
            // grade, for the reason AutobahnCourse.AppendLink records: level was 0.45 m out over 150 m,
            // and the road's own climb would have lifted the motorway's verge with it.
            builder.Straight(150f, AutobahnCourse.WeissjochGradeAtExit);

            // Round to the north and away. The 420 m of northward run is not scenery: it puts the
            // mountain's lowest leg six hundred metres clear of the carriageway, and MountainField
            // averages anything closer than 250 m — nine hundred metres of mountain blended into a road
            // at −30 m is the five-metre ridge AutobahnCourse.MergeOffset records, at twenty times the
            // size.
            builder.Turn(240f, -90f, 3f);
            builder.Straight(420f, 4f);

            // Onto the leg axis. Everything above this runs east–west, so the stack advances north.
            builder.Turn(300f, 86f, 4.5f);

            // The valley floor, and the last fuel before twelve kilometres of mountain. Here for the
            // reason Tankstelle Passfuß is where it is: a forecourt has to be poured flat and plants a
            // whole verge width of level ground around itself, and there is none of that on a stack.
            //
            // <b>240 m of it, and near enough level, because the first attempt was neither.</b> The pad
            // sat on 60 m of 5 % run-in either side, and a level platform beside a road climbing at 5 %
            // is a road climbing into its own shelf: the build reported 2.7 m of ground dropped onto the
            // carriageway 50 m short of the pumps and ten sampled points of terrain standing above the
            // asphalt. The pass records the same failure at its summit. A forecourt needs the road flat
            // as well as the ground.
            builder.Straight(120f, 0.4f);
            builder.AddFuelStation("Tankstelle Weissjochfuß", -1f);
            builder.Straight(120f, 0.4f);

            // Only now the climb, and eased into rather than started at full grade against the pad.
            builder.Straight(80f, 4f);

            // --- A. Long legs, open hairpins, spruce. The gentlest face on the road, because this is
            // the part seen from the motorway and a wall straight off the valley floor reads as a cliff
            // rather than as a mountain.
            float direction = Stack(builder, 4, 420f, 260f, 36f, 8f, -1f);

            // --- B. The legs shorten and the corners tighten as the trees thin. Ends at 466 m, which is
            // the tree line.
            direction = Stack(builder, 8, 360f, 220f, 32f, 9f, direction);

            builder.AddViewpoint("Waldkanzel");

            // --- The bore through the rock band, on a traverse of its own.
            //
            // It cannot go inside the stack, and the reason is arithmetic rather than taste: a stage-C
            // leg is 300 m of which the straight halves are 143 m each, and a 190 m bore with 60 m of
            // approach at each end needs 310. TunnelBuilder also sweeps its massif 40 m either side, so
            // a portal in a hairpin exit folds the body through itself. Real passes put their tunnels
            // between hairpin groups for both reasons.
            builder.Straight(PortalApproach, 7f);

            float tunnelStart = builder.Distance;
            builder.Straight(190f, 7f);
            builder.AddFeature(RoadFeatureKind.Tunnel, tunnelStart, builder.Distance, "Graugrattunnel");

            builder.Straight(PortalApproach, 7f);

            // --- C. Above the trees: bare rock, and the shortest legs so far. Ends at 647 m, the snow
            // line.
            direction = Stack(builder, 5, 300f, 190f, 28f, 9.5f, direction);

            // --- The avalanche gallery, the first in the world with a real reason for one: it stands at
            // 663 m, which is above the snow line, on the open side of the face.
            builder.Straight(PortalApproach, 8f);

            float galleryStart = builder.Distance;
            builder.Straight(140f, 8f);
            builder.AddFeature(RoadFeatureKind.Gallery, galleryStart, builder.Distance, "Lawinengalerie");

            builder.Straight(PortalApproach, 8f);

            // --- D. The snow. Tightest corners on the road and the last of the climb.
            Stack(builder, 9, 240f, 160f, 26f, 9.5f, direction);

            // --- The col. Two hundred and forty metres of run-out first, which is what carries the
            // forecourt clear of the stack: a level pad reaches a whole verge width in every direction,
            // and the pass records a summit platform dropping twenty metres of ground onto the
            // carriageway of the leg below it. The last hairpin here is 52 m away rather than the pass's
            // 40, and this straight puts another two hundred between them.
            builder.Straight(240f, 1.5f);

            builder.Straight(100f, 0.3f);
            builder.AddFuelStation("Tankstelle Weissjoch", -1f);
            builder.Straight(100f, 0.3f);

            // The view from the top is the road, and that is not a compromise — it is the only thing
            // there is. The valley is nine hundred metres below and kilometres away against a 600 m far
            // plane with the fog wall inside it, so a viewpoint here that faced outwards would face
            // nothing. What is inside half a kilometre is the last four hairpins, directly underneath.
            builder.AddViewpoint(ColName);

            // Past the col, and this is where the road used to stop. The Weissjochring hangs off the
            // end of it: near enough level, so the branch's mouth is laid on ground with no camber to
            // follow, and long enough that the fork is not standing in the forecourt behind it.
            builder.Straight(ColRunOut, EndGrade);
        }

        /// <summary>
        /// One stage: <paramref name="cycles"/> of leg-then-hairpin, alternating hand so the sweep
        /// cancels against the corner and the stack does not drift.
        /// </summary>
        /// <returns>
        /// The hand the next stage must start on. Returned rather than recomputed because getting it
        /// wrong turns the next stage back down the mountain, and nothing in the build would say so —
        /// the road would simply be shorter and lower than the table claims.
        /// </returns>
        private static float Stack(
            RoadCourseBuilder builder,
            int cycles,
            float legLength,
            float sweepRadius,
            float hairpinRadius,
            float gradePercent,
            float direction)
        {
            for (int i = 0; i < cycles; i++)
            {
                builder.Leg(legLength, sweepRadius, LegSweep * direction, gradePercent);
                builder.Turn(hairpinRadius, HairpinAngle * direction, HairpinGrade);

                direction = -direction;
            }

            return direction;
        }
    }
}
