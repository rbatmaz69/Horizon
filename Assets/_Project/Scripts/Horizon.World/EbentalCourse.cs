using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The country road on from the foot of the pass: an open valley, a crest, and a loop around a
    /// lake.
    ///
    /// <para><b>A continuation, laid out as the opposite of the road it continues.</b> The pass is
    /// tight, slow and steep — it lives on 20 m hairpins and 9.5 % grades, and it is authored around
    /// them. Nothing here turns tighter than 150 m and nothing climbs past 3 %. That is the whole
    /// point of a second environment: the same car, driven a completely different way. Sitting one
    /// after the other, each makes the other read as a place rather than as terrain.</para>
    ///
    /// <para><b>The profile deliberately rises again.</b> A road leaving a summit wants to be a slide,
    /// and a slide is one decision repeated for five kilometres. This falls to 16.5 m in the valley
    /// floor, climbs 18 m back to a crest at 34.4 m, and only then goes down for good. The climb is
    /// where the long straight is, so the fast part of the road is also the part that loads the
    /// engine.</para>
    ///
    /// <para><b>It ends provisionally, and that is deliberate too.</b> The run-out simply stops, the
    /// way the pass's own did until this road was built. What comes after is a later decision — so
    /// <see cref="EndPoint"/> and <see cref="EndHeading"/> are published the way
    /// <see cref="AutobahnCourse"/> and <see cref="CoastCourse"/> publish theirs, and the next stretch
    /// is grafted onto them instead of being fitted to them. The end faces 122°: south-east, with
    /// 2.4 km of empty ground between it and Hochstadt's arterial. That is the obvious next leg, and
    /// noting it here is cheaper than rediscovering it.</para>
    ///
    /// <para><b>No village.</b> <c>TownShape.LimitAcross</c> refuses a settlement deeper than
    /// 0.65 · R on its trunk road, and at the 180 m corner that is 117 m — not a place, a row of
    /// houses. A town on this road means giving it a straight axis of its own, the way Hochstadt and
    /// Seeburg have one.</para>
    /// </summary>
    public static class EbentalCourse
    {
        /// <summary>
        /// Radius of the tightest corner on the road, metres.
        ///
        /// <para>Tight for this road and enormous for the pass, which is the contrast the whole layout
        /// is built on. It is also the floor for anything added later: below about 150 m the road stops
        /// being a country road and starts being a second pass.</para>
        /// </summary>
        private const float TightRadius = 180f;

        /// <summary>
        /// Radius of the loop around the lake, metres.
        ///
        /// <para>150 m through 160° puts the two legs of the loop 185 m apart in plan. That gap is
        /// load-bearing twice over: near enough that the far side of the loop is visible across the
        /// water while driving it, and far enough that <see cref="MountainField"/> does not average
        /// the two carriageways into one shelf — its <c>InfluenceRadius</c> is 80 m and its coarse
        /// grid reaches 250 m.</para>
        /// </summary>
        private const float LoopRadius = 150f;

        private const float LoopAngle = 160f;

        /// <summary>
        /// Grade over the crest, percent. The steepest thing on the road, and still a third of what
        /// the pass climbs at.
        /// </summary>
        private const float CrestGrade = 2.6f;

        /// <summary>
        /// How hard the ground falls away on the far side of the crest, percent.
        ///
        /// <para>Steeper than the climb, and immediately into an 84° left. The corner exit is not
        /// visible until the car is over the edge, which is the one genuinely demanding moment on an
        /// otherwise open road. It is telegraphed rather than hidden: <c>DelineatorPostBuilder</c> puts
        /// a post line round the bend, and reading it from the crest is the skill being asked for.
        /// Flatten this and the corner becomes scenery.</para>
        /// </summary>
        private const float CrestFallGrade = -3f;

        static EbentalCourse()
        {
            // Walked once so the end is measured rather than looked up, exactly as CoastCourse measures
            // its own. Whatever is built on from here follows this road when it is retuned.
            var probe = new RoadCourseBuilder(
                MountainPassCourse.EndPoint, MountainPassCourse.EndHeading);

            Append(probe);

            RoadCourse walked = probe.Build();

            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = probe.HeadingDegrees;
        }

        /// <summary>Where the country road runs out. See the note on the class about what that means.</summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there. 0 faces +Z, increasing turns towards +X.</summary>
        public static float EndHeading { get; }

        /// <summary>
        /// Plan centre of the lake the loop wraps, in world x/z.
        ///
        /// <para>Derived from the loop rather than typed beside it: the centre of a constant-radius arc
        /// is one radius to the inside of where it starts, and that is the one place a lake can sit
        /// without the road running into it. <c>WaterShape.Corridor</c> reads this, so retuning
        /// <see cref="LoopRadius"/> moves the water with the road instead of leaving it behind.</para>
        /// </summary>
        public static Vector2 LakeCentre { get; private set; }

        /// <summary>
        /// The whole course, from the foot of the pass out into the Ebental.
        ///
        /// <para>No probe-and-graft solve is needed, for the same reason the coast road needs none:
        /// this starts where something else finished and runs on, so there is nothing to solve for.</para>
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(
                MountainPassCourse.EndPoint, MountainPassCourse.EndHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, so the probe in the static constructor and the real walk cannot drift
        /// apart. Two copies of a road are two roads.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- Out of the pass. The valley opens on the left and the Bergsee sits about 300 m off
            // the right-hand verge through the second bend — the first thing on this road that is not
            // mountain.
            builder.Straight(280f, -2.4f);
            builder.Turn(320f, -34f, -2f);
            builder.Straight(220f, -1.6f);

            // The one corner that has to be braked for. It comes early, while the pass is still in the
            // mirror, so the road states its own speed before the long stuff starts.
            builder.Turn(TightRadius, 72f, -1.2f);
            builder.Straight(340f, -0.8f);

            // --- Valley floor, the lowest point of the road, and the turn onto the climb.
            builder.Turn(260f, -58f, -0.4f);

            // --- The fast half kilometre. Uphill, which is what keeps it from being a place to simply
            // hold the throttle down.
            //
            // Split 180 + 340 so the filling station sits inside it rather than on either end. Both
            // divide by the 10 m point spacing, so the road is unchanged.
            builder.Straight(180f, 1.4f);

            // On the valley floor at the start of the fast stretch, which is the one genuinely open
            // piece of this road — everywhere else is either bending or on the crest. Left-hand side,
            // away from the Bergsee.
            builder.AddFuelStation("Auenhof", -1f);
            builder.Straight(340f, 1.4f);
            builder.Turn(400f, 46f, 2f);
            builder.Straight(160f, CrestGrade);
            builder.AddViewpoint("Hochwiese");

            // --- Over the top. See CrestFallGrade for why these two instructions are one idea.
            builder.Turn(220f, -84f, CrestFallGrade);
            builder.Straight(300f, -2.6f);

            // --- The loop. Split in half only so the viewpoint lands on its apex, over the water;
            // two 80° arcs of one radius are the same curve as one of 160°.
            LakeCentre = LoopCentre(builder);

            builder.Turn(LoopRadius, LoopAngle * 0.5f, -2f);
            builder.AddViewpoint("Auensee");
            builder.Turn(LoopRadius, LoopAngle * 0.5f, -2f);

            // --- Back down the far shore and out.
            builder.Straight(240f, -2.4f);
            builder.Turn(300f, -40f, -1.6f);
            builder.Straight(380f, -0.8f);
            builder.Turn(500f, 30f, -0.4f);
            builder.Straight(420f, -0.2f);

            // Somewhere to arrive at, so the provisional end reads as a place the road reaches rather
            // than as a road that was not finished.
            builder.AddViewpoint("Ebenkopf");
        }

        /// <summary>
        /// Centre of the loop, in plan, taken from wherever the builder currently stands.
        ///
        /// <para>Must be called immediately before the loop is walked. A right-hand arc turns about a
        /// point one radius to the right of the entry, which is what <see cref="LakeCentre"/> is.</para>
        /// </summary>
        private static Vector2 LoopCentre(RoadCourseBuilder builder)
        {
            float radians = builder.HeadingDegrees * Mathf.Deg2Rad;
            var forward = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 centre = builder.Position + right * LoopRadius;

            return new Vector2(centre.x, centre.z);
        }
    }
}
