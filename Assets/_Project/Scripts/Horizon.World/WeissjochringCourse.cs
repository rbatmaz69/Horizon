using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The Weissjochring: fourteen and a half kilometres of closed circuit on the shoulder below the
    /// col, and the first road in this world that is driven for its own sake rather than to get
    /// somewhere.
    ///
    /// <para><b>Why here and nowhere else.</b> A circuit wants height to play with, and the only place
    /// in this world that has any is the Weissjoch. It is also the only place where two hundred and
    /// sixty metres can be dropped and regained without leaving the massif or coming within half a
    /// kilometre of another road. Everything below follows from those two facts.</para>
    ///
    /// <para><b>The shape is a ladder, and that is a terrain decision rather than a styling one.</b>
    /// <c>TerrainShape.CorridorWidth</c> is 200 m: ground exists only that far from a road. A circuit
    /// with a real enclosed area — an oval, or anything shaped like a modern Grand Prix track — would
    /// have a hole a kilometre across in the middle of it, and nothing in the build would say so. So
    /// this one folds: six rungs running east–west, a hairpin's own diameter apart, with a long straight
    /// down the side joining the two loose ends. No point inside the loop is more than about 170 m from
    /// tarmac — <c>ValidateInfieldCoverage</c> measures it, and that check exists because a hole in an
    /// infield is near no road at all and every other check here is therefore blind to it.</para>
    ///
    /// <para><b>The fold is also what makes the mountain visible.</b> <see cref="MountainField"/> derives
    /// the ground from the roads, so two rungs 340 m apart with a hundred and seventy metres of height
    /// between them build a real hillside between themselves. That is the switchback stack's own
    /// mechanism — the pass runs its legs 40 m apart for a 65 % face — opened out by a factor of eight.
    /// From the climb out you look across at the descent below you, which inside a 600 m far plane is
    /// the only way altitude is ever visible here.</para>
    ///
    /// <para><b>Consecutive rungs are translates of one another, not mirror images.</b> Each rung snakes
    /// — a rung that did not would be a ruler — and a snake reversed is a snake mirrored, which would
    /// bring two rungs together by twice the amplitude in the middle and push them apart at the ends.
    /// The rung's whole snake therefore flips sign with each hairpin, which is what
    /// <c>hand</c> does in <see cref="Rung"/>. Without it the closest approach between two stretches of
    /// this circuit was 73 m; with it, 246.</para>
    ///
    /// <para><b>The height table is pinned to the region's own bands and not to taste.</b>
    /// <c>LandRegion.Weissjoch</c> puts the tree line at 700 m and the snow line at 600 m. A lap that
    /// runs 810 → 560 → 810 therefore crosses both of them twice: snowy rock at the top, dark spruce
    /// standing on white ground through the middle, green forest on the floor of the Kesselgrund. That
    /// band is the whole picture a winter region is for, and it is worth more than any corner on the
    /// table.</para>
    ///
    /// <para><b>The paddock is 96 m below the col, and that is what buys the profile.</b> Putting the
    /// start straight level with the col would have left the last kilometre of the lap climbing back to
    /// 906 m at eleven per cent — a main straight that is really a hill climb. Dropping the paddock
    /// instead costs an access road that descends, which is what an access road to a circuit on a
    /// shoulder does anyway, and it is why you look down on the place from the Weissjoch.</para>
    ///
    /// <para><b>It closes with <see cref="RoadCourseBuilder.Close"/> and is paved as a loop.</b> See
    /// that method for the two ways a self-closure fails silently, and <see cref="RoadCourse.IsClosed"/>
    /// for why the seam is a wrapped path rather than two ends butted together under the line.</para>
    /// </summary>
    public static class WeissjochringCourse
    {
        /// <summary>The circuit, and the name of everything on it.</summary>
        public const string CircuitName = "Weissjochring";

        /// <summary>The main straight, and where the start/finish line is drawn across it.</summary>
        public const string LineName = "Zielgerade";

        /// <summary>The fork where the road down from the col joins the circuit.</summary>
        public const string PitName = "Boxengasse";

        /// <summary>The paddock pump. See <see cref="Append"/> for why there is exactly one.</summary>
        public const string FuelName = "Tankstelle Weissjochring";

        /// <summary>The lowest point on the lap, and the one place on it under the tree line.</summary>
        public const string BottomName = "Kesselgrund";

        /// <summary>The fast hairpin at the far end, and the layby that watches it.</summary>
        public const string SummitName = "Gratkehre";

        /// <summary>
        /// Height of the paddock, absolute world metres.
        ///
        /// <para>An absolute number rather than a drop below the col, because what it has to agree with
        /// is <c>LandRegion.Weissjoch</c>'s two altitude bands and not the road above it. Everything on
        /// the lap is measured down from here, so the bands land where they are meant to whatever the
        /// motorway or the climb does. The access road's grade is what absorbs the difference — see
        /// <see cref="BuildAccess"/>, which reports it if it ever gets silly.</para>
        /// </summary>
        public const float PaddockElevation = 810f;

        /// <summary>
        /// Where the start line stands, in the Weissjoch's own end frame: metres to the right of its
        /// heading, then metres along it.
        ///
        /// <para><b>1600 m out to the left, and the first attempt at 360 put a race track on top of the
        /// mountain pass.</b> Everything else here is placed relative to the road it hangs off, and that
        /// is safe for a leg that carries on from somewhere — a leg cannot double back over a world it
        /// has not reached yet. A circuit is 2.4 km of footprint rather than a line, so it can, and this
        /// one did: its rungs ran two kilometres south of the col at 810 m, straight across
        /// <c>MountainPassCourse</c> a hundred metres above sea level. The build reported <b>terrain
        /// standing 674 m above the asphalt at 1709 points of the pass</b> — which is the one number
        /// that catches it, and it is reported against the road that was there first rather than against
        /// the one that arrived. <c>ValidateRoadClearance</c> on the circuit itself said nothing about
        /// it.</para>
        ///
        /// <para>So this is measured against every other road's plan bounds rather than against the col
        /// alone: the nearest carriageway to any part of the circuit is now 1.6 km away. That is far
        /// more clearance than <c>MountainField.CoarseReach</c> needs, and it costs nothing — the
        /// footprint lands inside the world's existing bounds in both axes, so the coarse height grid
        /// does not grow.</para>
        /// </summary>
        private const float LineAcross = -1600f;

        /// <summary>
        /// See <see cref="LineAcross"/>. Five hundred metres along the col's own heading, and the number
        /// belongs to the access road rather than to the circuit.
        ///
        /// <para>The access leaves the col heading one way and has to arrive at the pit mouth heading
        /// another, and a ninety-degree turn out of the col costs its own radius sideways before it has
        /// gone anywhere. At zero the mouth ended up on the wrong side of that swing, so the walk
        /// finished 340 m from its target facing 58° away from it — two poses that close together with
        /// a 260 m turning circle is the case <see cref="RoadCourseBuilder.Close"/> warns about, and the
        /// shortest Dubins family that existed came out <b>1935 m long</b>. The access road built at
        /// 3546 m instead of 1700, looped back through the circuit, and put a carriageway at 810 m
        /// within eighty metres of a rung at 649 — which the build reported as terrain standing 183 m
        /// above the asphalt at 312 points, on the circuit, with nothing anywhere saying the access road
        /// was the cause.</para>
        ///
        /// <para>At 500 the mouth sits on the side the swing already takes the road, and the solve is a
        /// corner and 130 m of straight.</para>
        /// </summary>
        private const float LineAlong = 500f;

        /// <summary>
        /// How far the main straight is turned off the road arriving from the col, degrees.
        ///
        /// <para>Zero: the straight runs parallel to the col's own run-out, a kilometre and a half to
        /// the side of it. That is what lets the access road come up square to the circuit and merge
        /// onto the straight in the direction the lap is driven — the alternative is an access that has
        /// to turn through a hundred and eighty degrees at the mouth, which is a hairpin at a pit
        /// exit.</para>
        /// </summary>
        private const float LineTurn = 0f;

        /// <summary>
        /// Level straight from the start of the authored straight to the pit mouth, metres.
        ///
        /// <para><b>The mouth comes first, then the pumps, then the grid, then the line.</b> That order
        /// is the whole of this stretch and the first version had it inside out: the line sat at
        /// distance zero with the mouth 180 m <i>after</i> it, which put the grid on the far side of the
        /// line — on the closure's climbing approach, and behind a car arriving from the col. Reaching
        /// pole meant turning round. A start you have to drive backwards to is not a start, and a grid
        /// on a five per cent slope is not a grid.</para>
        /// </summary>
        private const float StraightToFork = 120f;

        /// <summary>
        /// Level straight between the pit mouth and the pump, metres.
        ///
        /// <para>A hundred and forty, which is what the two clearances either side of it need:
        /// <c>GuardRailBuilder.JunctionClearance</c> is 60 m and its <c>ForecourtClearance</c> 45 m, and
        /// a fork and a forecourt sharing one stretch of verge is a rail that stops for both and stands
        /// for neither.</para>
        /// </summary>
        private const float ForkToFuel = 140f;

        /// <summary>
        /// Level straight from the pump to the start/finish line, metres.
        ///
        /// <para>Long enough that all twelve grid slots fall inside it — the back row is
        /// <c>CircuitMeshes.GridSlots / 2 × 16</c> = 96 m behind the line — with room between the last
        /// box and the forecourt's frontage.</para>
        /// </summary>
        private const float FuelToLine = 270f;

        /// <summary>
        /// Where the start/finish line is painted, metres along the course.
        ///
        /// <para>Everything that has to agree about the line reads this: the paint and the grid boxes,
        /// the grid's own poses, the lap timer's crossing plane, the spawn point, and the preview's
        /// cameras. It is a sum rather than a number so it cannot drift from the table above it.</para>
        /// </summary>
        public const float LineDistance = StraightToFork + ForkToFuel + FuelToLine;

        /// <summary>Level straight from the line to the first corner, metres.</summary>
        private const float LineToCorner = 220f;

        /// <summary>Radius of the two corners that turn the straight into the ladder, metres.</summary>
        private const float CornerRadius = 200f;

        /// <summary>
        /// Straight between that corner and the first rung, metres — and the number that trades the
        /// circuit's two spacing rules against each other.
        ///
        /// <para>It sets how far the rungs' near ends stand off the main straight. Too little and the
        /// hairpins at that end crowd the straight: at 140 m the closest approach anywhere on the lap
        /// was 186 m. Too much and the strip between the straight and the hairpins stops being ground
        /// at all: at 260 m the widest hole inside the loop was 251 m from tarmac against a 200 m
        /// corridor. Two hundred puts the closest approach at 246 m and leaves one pocket beside the
        /// paddock, which is where the paddock is.</para>
        /// </summary>
        private const float LadderStandoff = 200f;

        /// <summary>
        /// Radius of the hairpin at the end of a rung, metres — and therefore <b>half</b> the spacing of
        /// the whole ladder, since a hairpin's job here is to move the road one rung sideways.
        ///
        /// <para>170 gives 340 m between rungs. Under about 110 the rungs are inside each other's
        /// <c>MountainField.CoarseReach</c> and the ladder comes out as one flat table with lines on it;
        /// over about 200 the ground between them is outside the terrain corridor and there is no
        /// ladder, only six roads.</para>
        /// </summary>
        private const float HairpinRadius = 170f;

        /// <summary>Hairpins flatten off, for the reason the pass's and the Weissjoch's do.</summary>
        private const float HairpinGrade = 1.5f;

        /// <summary>
        /// Radius of the closing solve, metres. Generous on purpose: the closure is the last corner onto
        /// the main straight and the fastest part of the lap.
        /// </summary>
        private const float CloseRadius = 260f;

        /// <summary>
        /// Longest closure to accept, metres.
        ///
        /// <para>The solve is a ninety-degree corner and about eight hundred metres of straight, so it
        /// measures around 1150. 1600 leaves room for the table above to be retuned and still catches
        /// the three-hundred-degree Dubins family, which is the failure this number exists for — see
        /// <see cref="RoadCourseBuilder.Close"/>.</para>
        /// </summary>
        private const float CloseLimit = 1600f;

        /// <summary>
        /// How far off the trunk's heading the access road arrives at the pit mouth, degrees.
        ///
        /// <para>The same figure the Stadtfeld road leaves the Ebental on. Shallower and the mouth reads
        /// as a road that widens rather than as a junction; steeper and a car joining the straight has
        /// to stop to do it.</para>
        /// </summary>
        public const float ForkDeflection = 32f;

        /// <summary>
        /// Radius of the level ground the paddock asks for, metres — the apron the pit buildings and
        /// the grandstand stand on, handed to <c>MountainField</c> as level samples before the field is
        /// built.
        ///
        /// <para><b>120, and the first attempt at 190 was a mountain standing through the circuit.</b>
        /// Level samples behave exactly like road samples: they raise a shelf and the coarse field
        /// averages them out to <c>MountainField.CoarseReach</c>, 250 m. An apron pushed out towards the
        /// ladder therefore does not stop at its own rim — it drags the ground up for a quarter of a
        /// kilometre past it, and the rungs below are 250 m of descent away. The build reported terrain
        /// standing <b>162 m above the asphalt at 699 points</b> on a rung that ran straight through the
        /// disc. Anything level and large has this reach; the rule is the one the forecourts already
        /// follow, which is that a pad plants a whole verge width of level ground around itself and has
        /// to be given room for it.</para>
        ///
        /// <para><b>It is not doing double duty as terrain cover, and it was nearly asked to.</b> The
        /// worry was the pocket between the main straight and the near ends of the rungs, since level
        /// samples deliberately do not count towards <c>DistanceToRoad</c>. <c>ValidateInfieldCoverage</c>
        /// answered it: with <see cref="LadderStandoff"/> at 200 m the furthest point inside the whole
        /// loop is 192 m from tarmac against a 200 m corridor, so the roads cover their own infield and
        /// the apron only has to be an apron.</para>
        /// </summary>
        public const float PaddockRadius = 120f;

        /// <summary>
        /// How far along the main straight, past the line, the apron is centred — metres.
        ///
        /// <para>Centred <i>on</i> the road rather than offset to one side of it, for the reason
        /// <see cref="PaddockRadius"/> gives: a level disc reaches a quarter of a kilometre past its own
        /// rim, and every metre it is pushed towards the ladder is a metre closer to dragging a rung's
        /// ground up with it. On the road it is levelling ground the carriageway had already levelled.
        /// </para>
        ///
        /// <para>Past the line rather than behind it, because behind the line is the closure's climbing
        /// approach and an apron wants the level stretch. The pits and the grandstand sit inside this
        /// same window.</para>
        /// </summary>
        private const float PaddockAlong = LineDistance - 20f;

        /// <summary>
        /// Which hand the infield is on, seen from a car on the main straight: +1 right, −1 left.
        ///
        /// <para>A fact about the lap's plan rather than about any one point on it — the ladder is
        /// entered by turning <i>left</i> off the straight, so everything the circuit encloses is to the
        /// left. The pits and the grandstand are placed against it, and the pump is put on the other
        /// side.</para>
        /// </summary>
        public const float PaddockSide = -1f;

        /// <summary>
        /// One rung of the ladder: a straight-ish run across the mountain that ends at a given height.
        ///
        /// <para>The height is a target rather than a grade because what the table is really saying is
        /// where the tree line and the snow line fall on the lap. A grade would have to be re-derived by
        /// hand every time a rung's length moved, and the bands would drift without anything saying so.</para>
        /// </summary>
        private readonly struct Rung
        {
            public readonly string Name;

            /// <summary>Snake cycles. Even, or the rung ends off its own axis.</summary>
            public readonly int Cycles;

            public readonly float LegLength;

            /// <summary>Radius of the snake's sweeps. Bigger is faster and flatter.</summary>
            public readonly float SweepRadius;

            /// <summary>
            /// Sweep angle, degrees. With the radius it sets the snake's amplitude, which is
            /// <c>2 R (1 − cos a)</c> and must stay well under the ladder's spacing: the rungs hold
            /// their phase, so what two neighbours actually vary by is the <i>difference</i> of their
            /// amplitudes. The table below spans 41 to 55 m.
            /// </summary>
            public readonly float SweepAngle;

            /// <summary>Absolute world metres to arrive at.</summary>
            public readonly float EndElevation;

            public Rung(string name, int cycles, float legLength, float sweepRadius, float sweepAngle,
                float endElevation)
            {
                Name = name;
                Cycles = cycles;
                LegLength = legLength;
                SweepRadius = sweepRadius;
                SweepAngle = sweepAngle;
                EndElevation = endElevation;
            }

            /// <summary>Road length of the rung. Two legs a cycle.</summary>
            public float Road => Cycles * 2f * LegLength;
        }

        /// <summary>
        /// The six rungs, in the order they are driven: down through the tree line, along the bottom,
        /// and back up out of it.
        ///
        /// <para>All six are the same length, and the variety is in the corners and in what is growing
        /// beside them. The two at the top are fast and in the snow; the two in the middle are the
        /// tightest on the lap and are the only stretches under the tree line; the two climbing out open
        /// up again. That is the Weissjoch's own "four stages are four places" argument, told with
        /// altitude instead of with switchbacks.</para>
        /// </summary>
        private static readonly Rung[] Rungs =
        {
            new Rung("Firnkurve", 4, 200f, 420f, 18f, 720f),
            new Rung("Sprungkuppe", 4, 200f, 320f, 22f, 645f),
            new Rung("Waldbogen", 4, 200f, 200f, 30f, 585f),
            new Rung(BottomName, 4, 200f, 180f, 32f, 562f),
            new Rung("Steinbogen", 4, 200f, 280f, 24f, 650f),
            new Rung(SummitName, 4, 200f, 400f, 20f, 745f),
        };

        /// <summary>
        /// Walks the lap once as a measurement, so the pit mouth's pose exists before the access road is
        /// built against it and before anything is paved.
        ///
        /// <para>The probe and the real walk both go through <see cref="Append"/> and nothing else. Two
        /// copies of a road are two roads, and a fork is the one thing two courses have to agree
        /// about.</para>
        /// </summary>
        static WeissjochringCourse()
        {
            Quaternion frame = Quaternion.Euler(0f, WeissjochCourse.EndHeading, 0f);

            Vector3 line = WeissjochCourse.EndPoint
                           + frame * Vector3.right * LineAcross
                           + frame * Vector3.forward * LineAlong;

            line.y = PaddockElevation;

            StartPoint = line;
            StartHeading = WeissjochCourse.EndHeading + LineTurn;

            var probe = new RoadCourseBuilder(StartPoint, StartHeading);

            Append(probe);

            LapLength = probe.Build().PlannedLength;

            Quaternion lineFrame = Quaternion.Euler(0f, StartHeading, 0f);

            Vector3 paddock = StartPoint + lineFrame * Vector3.forward * PaddockAlong;

            paddock.y = PaddockElevation;
            PaddockCentre = paddock;
        }

        /// <summary>Middle of the paddock apron. See <see cref="PaddockRadius"/> for what it is for.</summary>
        public static Vector3 PaddockCentre { get; }

        /// <summary>The start/finish line: where the lap begins, and where the circuit is spawned at.</summary>
        public static Vector3 StartPoint { get; }

        /// <summary>Heading at the line, in the builder's convention.</summary>
        public static float StartHeading { get; }

        /// <summary>Where the access road meets the circuit. Read from the walk, never typed.</summary>
        public static Vector3 JunctionPoint { get; private set; }

        /// <summary>The circuit's own heading at the mouth.</summary>
        public static float JunctionHeading { get; private set; }

        /// <summary>The finished lap, closure included.</summary>
        public static float LapLength { get; }

        /// <summary>The circuit. Closed, and therefore paved as a loop.</summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(StartPoint, StartHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The road from the col down to the pit mouth.
        ///
        /// <para>Its own course rather than a tail on <see cref="WeissjochCourse"/>, because it is the
        /// branch of a fork and <c>TrunkForkBuilder</c> lays a mouth between two paths. It descends the
        /// whole way — see <see cref="PaddockElevation"/> — and it swings west first so that it, and the
        /// circuit behind it, stay clear of the switchback stack's own shelf.</para>
        /// </summary>
        public static RoadCourse BuildAccess()
        {
            var builder = new RoadCourseBuilder(
                WeissjochCourse.EndPoint, WeissjochCourse.EndHeading);

            // Two ribbons that touch have to agree about their grade or the join is a step. The col's
            // run-out is near enough level, so this starts near enough level too and only then falls.
            builder.Straight(160f, WeissjochCourse.EndGrade);

            float fall = WeissjochCourse.EndPoint.y - PaddockElevation;

            if (fall < 40f || fall > 200f)
            {
                Debug.LogError(
                    $"[Horizon] The Weissjochring's access road has {fall:0} m to lose between the col "
                    + $"and a paddock fixed at {PaddockElevation:0} m. That height is pinned to the "
                    + "region's tree and snow lines rather than to the road above it, so if the climb "
                    + "has been retuned it is this road's length that has to move, not the paddock's "
                    + "height — the bands are the whole reason the lap sits where it does.");
            }

            // Square off the col and out across the shoulder. A kilometre and a half of it, because
            // that is how far the circuit had to be moved to stop standing on the mountain pass — see
            // LineAcross. It is also what turns 96 m of descent into a four per cent road instead of the
            // fifteen per cent a short one would need.
            // The grades here are chosen so the walk arrives at the mouth already at the paddock's own
            // height, within a couple of metres. That matters more than it looks: ConnectTo derives one
            // uniform grade across the whole solve, so every metre still to be lost when the authored
            // part ends is a metre the last few hundred have to take. At -4.5 the branch reached the
            // mouth at 8.4 % against a main straight that is level by construction, MountainField
            // averaged the two shelves across the throat, and the build reported 0.68 m of terrain
            // standing on the carriageway at the pit exit — which is a jump, once a lap, at the one
            // place a car is accelerating. AddJunction's rule that a fork wants level track is not only
            // about the trunk.
            builder.Turn(300f, -90f, -6f);
            builder.Straight(1000f, -6.6f);

            float before = builder.Distance;

            // Arrives on the circuit's own heading less the deflection, so a car coming down from the
            // col turns onto the main straight in the direction the lap is driven. The pose is read from
            // the circuit's walk; a literal here would be a fork that moved on one road and not the
            // other.
            builder.ConnectTo(JunctionPoint, JunctionHeading - ForkDeflection, 260f);

            float connected = builder.Distance - before;

            if (connected > 1100f)
            {
                Debug.LogError(
                    $"[Horizon] The Weissjochring access road's closing solve came out {connected:0} m "
                    + "long against a 1100 m limit. ConnectTo takes the shortest Dubins family that "
                    + "exists and one of them loops the long way round, so this is an access road with "
                    + "a circle in it rather than one that failed to build. Retune the swing above so "
                    + "the walk ends nearer the mouth and closer to its heading.");
            }

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, kept apart from <see cref="Build"/> so the probe in the static constructor
        /// and the real walk cannot drift apart.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- The main straight. Level over its whole authored length, which is what both the fork
            // and the start line need: the throat is laid on top of the carriageway and the line is
            // painted on it, and laid-on paving only sits flush where the surface under it has no camber
            // to follow. The straight's *approach* climbs, and that is the closure's business.
            builder.Straight(StraightToFork, 0f);

            JunctionPoint = builder.Position;
            JunctionHeading = builder.HeadingDegrees;
            builder.AddJunction(PitName);

            builder.Straight(ForkToFuel, 0f);

            // The only pump on the circuit, and one is right. A lap is fifteen kilometres and a tank
            // driven hard is a good deal more than that, so a car that starts the lap full finishes it;
            // what a circuit needs is fuel in its paddock, not a filling station every six kilometres
            // round a race track. ValidateFuelStations is told this course is a loop for that reason.
            builder.AddFuelStation(FuelName, 1f);

            builder.Straight(FuelToLine, 0f);

            // The line itself. Nothing is marked on the course here — it is a painted line and a gantry,
            // not a RoadFeature — but everything downstream measures from LineDistance, and this is
            // where the walk agrees with it.
            builder.Straight(LineToCorner, 0f);

            // --- Into the ladder. The corner falls gently so the rungs start below the paddock rather
            // than on its shelf.
            builder.Turn(CornerRadius, -90f, -1.5f);
            builder.Straight(LadderStandoff, -2.5f);

            // Left off the straight, so the ladder advances away from everything else in the world.
            // See PaddockSide, which is the same fact stated for the things standing beside the road.
            float hand = -1f;

            for (int i = 0; i < Rungs.Length; i++)
            {
                Rung rung = Rungs[i];

                if (rung.Name == BottomName || rung.Name == SummitName)
                {
                    builder.AddViewpoint(rung.Name);
                }

                float grade = (rung.EndElevation - builder.Elevation) / rung.Road * 100f;

                AppendRung(builder, rung, grade, hand);

                if (i == Rungs.Length - 1)
                {
                    break;
                }

                builder.Turn(HairpinRadius, 180f * hand, HairpinGrade);
                hand = -hand;
            }

            // --- Out of the last rung and back to the line. The closure lays the final corner and the
            // climbing approach to the straight: about eleven hundred metres, and the one stretch of
            // this circuit whose shape is a solve rather than a table.
            builder.Straight(LadderStandoff, 4f);
            builder.Close(CloseRadius, CloseLimit);
        }

        /// <summary>
        /// One rung: pairs of opposite sweeps, so the road snakes but holds its own axis.
        ///
        /// <para>An S-pair returns the heading to the axis but displaces the road sideways by
        /// <c>2 R (1 − cos a)</c>, so the sign of the pair has to alternate or a rung walks off its own
        /// line — which is what turned the first ladder built here into a fan.</para>
        /// </summary>
        /// <param name="hand">
        /// Flips with each hairpin. It is what makes the next rung a <i>translate</i> of this one rather
        /// than its mirror image, and therefore what keeps the two the ladder's spacing apart over their
        /// whole length instead of only at their ends.
        /// </param>
        private static void AppendRung(RoadCourseBuilder builder, in Rung rung, float grade, float hand)
        {
            for (int i = 0; i < rung.Cycles; i++)
            {
                float sign = (i % 2 == 0 ? 1f : -1f) * hand;

                builder.Leg(rung.LegLength, rung.SweepRadius, rung.SweepAngle * sign, grade);
                builder.Leg(rung.LegLength, rung.SweepRadius, -rung.SweepAngle * sign, grade);
            }
        }
    }
}
