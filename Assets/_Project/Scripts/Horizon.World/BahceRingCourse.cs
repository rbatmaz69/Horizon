using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The Bahçe Ring: five and a third kilometres of closed circuit in the orchard valley east of
    /// Yalıköy, and the second road in this world driven for its own sake rather than to get somewhere.
    ///
    /// <para><b>The layout is Istanbul Park, measured rather than remembered.</b> The reference plan was
    /// traced to its centreline, scaled so the lap comes to the real circuit's 5338 m, and reduced to
    /// the fourteen corners below. Two independent facts fell out of that trace and both are worth
    /// keeping in mind before anything here is retuned: the net turn came to −360.0° over the lap, and
    /// the corners split eight left and six right — which is exactly the count the real circuit
    /// publishes. The shape is therefore not an impression of the place; the arithmetic checked itself.
    /// The signature corner, the long quadruple-apex left, is <see cref="Turn8Name"/>.</para>
    ///
    /// <para><b>An enclosed circuit was supposed to be impossible here, and the measurement is what said
    /// otherwise.</b> <c>TerrainShape.CorridorWidth</c> is 200 m — ground is authored only that far from
    /// a road — and that is the whole reason <see cref="WeissjochringCourse"/> is folded into a ladder
    /// instead of being shaped like a race track. Istanbul Park is not an oval: it doubles back on itself
    /// twice, so at full scale the furthest point inside the loop is about 280 m from tarmac and, more to
    /// the point, <i>every terrain tile the loop encloses is one <c>TerrainTileBuilder.ListTiles</c>
    /// already asks for</i>. There is no hole. See <c>ValidateInfieldCoverage</c>, which was measuring
    /// the corridor as a proxy for that and now measures the thing itself.</para>
    ///
    /// <para><b>The corner radii are fitted, not typed, and the closure is why.</b> A traced polyline
    /// carries a few per cent of drift, and a few per cent over five kilometres is a lap that ends four
    /// hundred metres from where it began. Every angle below is the measurement; the radii and the
    /// straights were then solved — as a constrained least-squares against the measured values, so none
    /// of them moved far — until the walk ends 300 m short of the line and <i>on its heading</i>, which
    /// is a closure <see cref="RoadCourseBuilder.Close"/> lays as a straight. Change an angle and the
    /// fit has to be redone; that is what <see cref="CloseLimit"/> is standing there to catch, and it
    /// has already caught it once — see <see cref="Corners"/>.</para>
    ///
    /// <para><b>The valley is 30 m deep and that is the profile's whole budget.</b> The real circuit
    /// falls about forty metres from the pit straight into Turn 1 and climbs back over its last sector.
    /// Here the main straight is level at <see cref="PaddockElevation"/> — it has to be, because the fork
    /// mouth, the start line and twelve grid boxes are all laid <i>on</i> it and laid-on paving only sits
    /// flush where the surface under it has no camber to follow — and everything after Turn 1 falls to
    /// 30 m at the exit of the eighth corner before the long back straight climbs it all back.</para>
    ///
    /// <para><b>Why here.</b> The whole south-eastern quadrant of the world is empty: no road, no water,
    /// no region, nothing south of z ≈ 1400 in the eastern half. The footprint lands inside the world's
    /// existing bounds in both axes, so the coarse height grid does not grow, and the nearest carriageway
    /// to any part of it is the Yalıköy leg 1.8 km north. <see cref="LineAcross"/> records why that is
    /// measured against every road's plan bounds rather than against the one this hangs off.</para>
    ///
    /// <para><b>And it is in flower.</b> <c>LandRegion.Bahce</c> is the reason this is not simply another
    /// stretch of Anadolu with kerbs on it — see there for what a region costs and what it buys.</para>
    /// </summary>
    public static class BahceRingCourse
    {
        /// <summary>The circuit, and the name of everything on it.</summary>
        public const string CircuitName = "Bahçe Ring";

        /// <summary>The main straight.</summary>
        public const string LineName = "Ana Düzlük";

        /// <summary>The fork the access road comes in on. See <c>TrunkForkBuilder</c>.</summary>
        public const string PitName = "Pit Yolu";

        /// <summary>The one pump on the lap.</summary>
        public const string FuelName = "Bahçe Ring Benzinlik";

        /// <summary>
        /// The eighth corner: the long left the real circuit is known for, and the only place on this lap
        /// where a car is turning for a third of a kilometre.
        /// </summary>
        public const string Turn8Name = "Sekizinci Viraj";

        /// <summary>The slowest corner on the lap, at the far end of the back straight.</summary>
        public const string SlowName = "Kiraz Bakışı";

        /// <summary>The layby in the blossom, on the access road rather than on the circuit.</summary>
        public const string GroveName = "Kiraz Bahçesi";

        /// <summary>
        /// Height of the main straight, in absolute metres.
        ///
        /// <para>Absolute, like the Weissjochring's, and low on purpose. Yalıköy runs out at 90 m on its
        /// plateau, so a paddock at 60 gives the access road thirty metres to lose over two and a half
        /// kilometres — a gradient nobody notices — and leaves the lap's own low point at 30 m, which is
        /// still well clear of sea level and of anything <c>WaterPlanner</c> put in the east.</para>
        ///
        /// <para>Every metre of this region is therefore below the world's tree line of 160 m, which is
        /// exactly why <c>LandRegion.Bahce</c> carries no altitude bands of its own. See there.</para>
        /// </summary>
        public const float PaddockElevation = 60f;

        /// <summary>
        /// Where the start line stands, measured across Yalıköy's own end heading.
        ///
        /// <para><b>A circuit is a footprint and not a line, and that is the difference that has already
        /// cost this project once.</b> Every other road here is placed relative to the one it leaves,
        /// which is safe for a leg carrying on from somewhere — a leg cannot double back over a world it
        /// has not reached yet. The Weissjochring could and did, and the build reported it as terrain
        /// standing 674 m above the asphalt <i>of the mountain pass</i>: the complaint arrives on the
        /// road that was there first, and <c>ValidateRoadClearance</c> on the new road says nothing at
        /// all.</para>
        ///
        /// <para>So this pair is chosen against every other road's plan bounds. The result is a footprint
        /// running x 12090…13170, z −2250…−410, whose nearest neighbour is the Yalıköy leg 1.8 km north
        /// and whose nearest water is the Yalı Koyu another two kilometres past that. It sits inside the
        /// world's existing extent in both axes, so it costs streaming and not a wider height grid.</para>
        /// </summary>
        private const float LineAcross = -300f;

        /// <summary>How far south of Yalıköy's end the line stands. See <see cref="LineAcross"/>.</summary>
        private const float LineAlong = 2200f;

        /// <summary>The main straight runs parallel to the road that leads here. Nothing needs it to.</summary>
        private const float LineTurn = 0f;

        /// <summary>Line zero to the pit mouth.</summary>
        private const float StraightToFork = 100f;

        /// <summary>
        /// Mouth to pump. <c>GuardRailBuilder</c> keeps 60 m clear of a junction and 45 m of a forecourt,
        /// so this cannot go under about 105 without the rails between them vanishing entirely.
        /// </summary>
        private const float ForkToFuel = 140f;

        /// <summary>
        /// Pump to the line, and the number that decides whether the grid fits.
        /// <c>CircuitMeshes.GridSlot</c> puts the twelfth box 104 m behind the line; at anything under
        /// about 150 the back row is parked on the forecourt.
        /// </summary>
        private const float FuelToLine = 190f;

        /// <summary>
        /// Where the lap begins and everything downstream measures from: the paint, the twelve grid
        /// poses, the timing plane, the spawn point and the preview cameras. A sum rather than a number
        /// so it cannot drift away from the walk above it.
        /// </summary>
        public const float LineDistance = StraightToFork + ForkToFuel + FuelToLine;

        /// <summary>Line to the first corner.</summary>
        private const float LineToCorner = 95f;

        /// <summary>
        /// How far the access road's mouth is deflected off the main straight.
        ///
        /// <para><b>Eighteen degrees, where every other fork in this world uses thirty-two, and the
        /// difference is what a pit road is.</b> Thirty-two is a country fork: the branch arrives
        /// pointing across the carriageway and its last twenty-five metres of paving lie over the
        /// racing line, which is exactly how it was reported — a road that does not meet the track
        /// flush but runs straight into the middle of it. A pit lane comes alongside and blends in.</para>
        ///
        /// <para><see cref="StadtfeldCourse"/>'s thirty-two is not a rule about forks, it is a rule
        /// about <i>height</i>: two branches close together share one shelf whatever the plan says, so
        /// they have to agree about their elevation while they are near each other, and a shallow angle
        /// keeps them near each other for longer. Here they agree by construction — the main straight is
        /// level at <see cref="PaddockElevation"/> and the access road arrives on it — so the reason for
        /// the wider angle is not present and the cost of it is.</para>
        ///
        /// <para>It cannot go much below this either: <c>TrunkForkBuilder.ThroatLength</c> is 70 m, and
        /// a branch at eighteen degrees crosses a nineteen-metre carriageway over sixty-one metres of
        /// its own length. Under about sixteen degrees the far corner of the overlap comes out from
        /// under the throat, which is the one place the z-fighting it exists to hide would show.</para>
        /// </summary>
        public const float ForkDeflection = 18f;

        /// <summary>
        /// Radius of the closing solve, and the ceiling on what it may come out as.
        ///
        /// <para><b>The ceiling is the point, and it has already earned its keep.</b> <c>ConnectTo</c>
        /// takes the shortest Dubins family that exists and <c>TurnBy</c> deliberately goes the long way
        /// round rather than crossing zero, so a closure that cannot be made gently is made as a full
        /// loop of carriageway instead — geometrically exact, arriving on the line to the millimetre,
        /// logging nothing and validating cleanly. It came out at 1965 m the first time this was built.
        /// The honest solve is 300 m, and the limit is set near enough to that to catch the next
        /// one.</para>
        /// </summary>
        private const float CloseRadius = 260f;

        /// <summary>See <see cref="CloseRadius"/>.</summary>
        private const float CloseLimit = 600f;

        /// <summary>Radius of the level apron the paddock stands on. See <c>AddPaddockSamples</c>: level
        /// samples raise a shelf exactly as road samples do and the coarse field averages them out to
        /// 250 m, which is why this is 120 and not the 190 that put a hillside through a circuit.</summary>
        public const float PaddockRadius = 120f;

        /// <summary>Centre of the apron, twenty metres before the line.</summary>
        private const float PaddockAlong = LineDistance - 20f;

        /// <summary>
        /// Which hand the infield is on, seen from a car on the main straight: −1 is left.
        ///
        /// <para>A fact about the plan and not about any one point on it, which is why
        /// <c>CircuitMeshes.Append</c> is told rather than left to work it out. Turn 1 is a left, so the
        /// lap and everything in it lies to the left of the straight; the pits stand on that side and the
        /// grandstand opposite.</para>
        /// </summary>
        public const float PaddockSide = -1f;

        /// <summary>How far down the access road the blossom layby stands.</summary>
        private const float GroveAlong = 980f;

        /// <summary>
        /// Where the region begins along the access road.
        ///
        /// <para>The road leaves Yalıköy in Anadolu's dry hillside and arrives in an orchard valley, and
        /// nothing about the tarmac says where one becomes the other. This is the same use
        /// <c>LandRegion.Anadolu</c> makes of it at the eastern anchorage of the Meerenge bridge: what
        /// separates two countries is a stretch of the same road.</para>
        /// </summary>
        public const float RegionStartAlong = 400f;

        /// <summary>
        /// The fourteen corners, in the order they are driven.
        ///
        /// <para>Angle is the measurement off the reference plan; radius is what the closure fit settled
        /// on. Positive turns right. See the class note for why neither may be edited without redoing
        /// the fit.</para>
        ///
        /// <para><b>These angles sum to exactly −360°, and that is a requirement rather than an
        /// observation.</b> The first version stopped at the fourteenth corner and left the last 36° to
        /// <see cref="RoadCourseBuilder.Close"/>. The walk then ended three hundred metres short of the
        /// line but pointing 36° off it, and <c>TurnBy</c> deliberately goes the long way round rather
        /// than crossing zero — so the shortest family that existed took the road most of the way round
        /// a 260 m circle and the closure came out <b>1965 m</b>. The lap built at 6.99 km instead of
        /// 5.36, ran a loop of carriageway through the access road's corridor, and the only thing that
        /// said so was <see cref="CloseLimit"/>. A closure asked to change the heading is a closure
        /// asked to gamble; asked only to cover ground, it is a straight line.</para>
        /// </summary>
        private static readonly Corner[] Corners =
        {
            new Corner(107f, -83f, -3.0f),   // 1  — downhill left off the straight
            new Corner(220f, 95f, -2.4f),    // 2  — the long right, still falling
            new Corner(96f, -96f, -1.2f),    // 3
            new Corner(72f, 103f, 0.0f),     // 4
            new Corner(82f, -161f, 1.0f),    // 5/6 — the tight double the plan draws as one switch
            new Corner(83f, 165f, 0.0f, 363f, 0.8f),    // 7  — after the first long link
            new Corner(103f, -66f, -2.2f, 125f, -1.6f), // 7b
            new Corner(131f, -143f, -2.0f, 55f, -2.5f), // 8  — the signature left
            new Corner(79f, -93f, 1.2f, 490f, -1.5f),   // 9  — after the run down from Turn 8
            new Corner(203f, 36f, 2.0f),     // 10
            new Corner(240f, 41f, 2.0f, 318f, 2.6f),    // 11 — onto the back straight
            new Corner(63f, -128f, 1.0f, 730f, 1.7f),   // 12 — the slowest corner on the lap
            new Corner(72f, 65f, 0.8f),      // 13
            new Corner(45f, -59f, 0.5f),     // 14
            // 14b. The tail of the fourteenth corner, which the reference plan draws as one long
            // opening bend onto the straight and which the trace resolves into three arcs. Only the
            // first is here; the other two sum to −4° and are the closure's to lay. It is what brings
            // the table to −360° — see the note above for what leaving it out cost.
            new Corner(57f, -36f, 0.5f),
        };

        /// <summary>
        /// One corner and the straight that leads into it.
        ///
        /// <para>The straight belongs to the corner rather than sitting between two of them, because the
        /// closure fit moves the two together and a table where they are separate rows is a table where
        /// half of it can be edited without the other half noticing.</para>
        /// </summary>
        private readonly struct Corner
        {
            public readonly float Radius;
            public readonly float Angle;
            public readonly float Grade;
            public readonly float Approach;
            public readonly float ApproachGrade;

            public Corner(float radius, float angle, float grade,
                float approach = 0f, float approachGrade = 0f)
            {
                Radius = radius;
                Angle = angle;
                Grade = grade;
                Approach = approach;
                ApproachGrade = approachGrade;
            }
        }

        /// <summary>
        /// Walks the lap once as a measurement, so the pit mouth's pose exists before the access road is
        /// built against it and before anything is paved. The probe and the real walk both go through
        /// <see cref="Append"/> and nothing else — two copies of a road are two roads, and a fork is the
        /// one thing two courses have to agree about.
        /// </summary>
        static BahceRingCourse()
        {
            Quaternion frame = Quaternion.Euler(0f, YalikoyCourse.EndHeading, 0f);

            Vector3 line = YalikoyCourse.EndPoint
                           + frame * Vector3.right * LineAcross
                           + frame * Vector3.forward * LineAlong;

            line.y = PaddockElevation;

            StartPoint = line;
            StartHeading = YalikoyCourse.EndHeading + LineTurn;

            var probe = new RoadCourseBuilder(StartPoint, StartHeading);

            Append(probe);

            LapLength = probe.Build().PlannedLength;

            Quaternion lineFrame = Quaternion.Euler(0f, StartHeading, 0f);

            Vector3 paddock = StartPoint + lineFrame * Vector3.forward * PaddockAlong;

            paddock.y = PaddockElevation;
            PaddockCentre = paddock;
        }

        /// <summary>Middle of the paddock apron. See <see cref="PaddockRadius"/>.</summary>
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
        /// The road from the end of Yalıköy down to the pit mouth.
        ///
        /// <para>Its own course rather than a tail on <see cref="YalikoyCourse"/>, because it is the
        /// branch of a fork and <c>TrunkForkBuilder</c> lays a mouth between two paths. It opens on
        /// <c>YalikoyCourse.EndGrade</c> for the reason that constant exists: two ribbons that touch and
        /// disagree about their grade meet in a step.</para>
        ///
        /// <para>It comes in from the <i>outside</i> of the lap, which is not a free choice. North of the
        /// start line there is no circuit at all — the closure arrives from the south-east — so a road
        /// coming down the west side reaches the mouth without crossing tarmac. The infield is on the
        /// other hand, and a road that wanted to be in it would have to go under the track.</para>
        /// </summary>
        public static RoadCourse BuildAccess()
        {
            var builder = new RoadCourseBuilder(
                YalikoyCourse.EndPoint, YalikoyCourse.EndHeading);

            builder.Straight(200f, YalikoyCourse.EndGrade);

            float fall = YalikoyCourse.EndPoint.y - PaddockElevation;

            if (fall < 10f || fall > 120f)
            {
                Debug.LogError(
                    $"[Horizon] The Bahçe Ring's access road has {fall:0} m to lose between the end of "
                    + $"Yalıköy and a paddock fixed at {PaddockElevation:0} m. That height is the "
                    + "circuit's own datum and the lap's whole profile hangs off it, so if the plateau "
                    + "above has been retuned it is this road's length that has to move, not the "
                    + "paddock's height.");
            }

            builder.Straight(1150f, -1.4f);

            builder.AddViewpoint(GroveName);

            float before = builder.Distance;

            builder.ConnectTo(JunctionPoint, JunctionHeading - ForkDeflection, 320f);

            float connected = builder.Distance - before;

            if (connected > 1300f)
            {
                Debug.LogError(
                    $"[Horizon] The Bahçe Ring access road's closing solve came out {connected:0} m "
                    + "long against a 1300 m limit. ConnectTo takes the shortest Dubins family that "
                    + "exists and one of them loops the long way round, so this is an access road with "
                    + "a circle in it rather than one that failed to build. Retune the run above so the "
                    + "walk ends nearer the mouth and closer to its heading.");
            }

            // The branch's own copy of the fork, at the mouth. The circuit carries one too, and both are
            // needed: every builder that clears a junction reads IsJunction off the course it is
            // building. Without this the pit road's rails and posts stop only where the road does,
            // which is on the racing line.
            builder.AddJunction(PitName);

            return builder.Build();
        }

        /// <summary>
        /// The lap. Order along the main straight is mouth, pump, grid, line, first corner — see
        /// <c>WeissjochringCourse.Append</c> for what putting the line at distance zero costs: the grid
        /// ends up on the far side of it, on the closure's approach, and reaching pole means turning
        /// round.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- The main straight. Level over its whole authored length, which is what the fork, the
            // start line and the twelve grid boxes all need: every one of them is laid on top of the
            // carriageway, and laid-on paving only sits flush where the surface under it has no camber
            // to follow. The straight's *approach* climbs, and that is the closure's business.
            builder.Straight(StraightToFork, 0f);

            JunctionPoint = builder.Position;
            JunctionHeading = builder.HeadingDegrees;
            builder.AddJunction(PitName);

            builder.Straight(ForkToFuel, 0f);

            // One pump, in the paddock, and one is right: a lap is well inside a tank driven hard, and
            // three stations round a race track to satisfy a rule written for a country road would be
            // ValidateFuelStations wearing the costume of a feature. It is told this course is a loop.
            builder.AddFuelStation(FuelName, 1f);

            builder.Straight(FuelToLine, 0f);

            builder.Straight(LineToCorner, 0f);

            for (int i = 0; i < Corners.Length; i++)
            {
                Corner corner = Corners[i];

                if (corner.Approach > 0f)
                {
                    builder.Straight(corner.Approach, corner.ApproachGrade);
                }

                // The two the previews are pointed at, and both are corners rather than laybys: a
                // viewpoint on a race track is somewhere to stop and watch one, which is what the
                // clearing VegetationShape.ViewpointClearing opens is for.
                if (i == 7)
                {
                    builder.AddViewpoint(Turn8Name);
                }
                else if (i == 11)
                {
                    builder.AddViewpoint(SlowName);
                }

                builder.Turn(corner.Radius, corner.Angle, corner.Grade);
            }

            // --- Back onto the straight. Three hundred metres, already on the straight's own heading,
            // and the one stretch of this circuit whose shape is a solve rather than a table. See
            // Corners for why it is asked to cover ground and not to turn.
            builder.Close(CloseRadius, CloseLimit);
        }
    }
}
