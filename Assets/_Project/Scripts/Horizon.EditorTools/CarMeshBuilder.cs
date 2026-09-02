using System.Collections.Generic;
using Horizon.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Horizon.EditorTools
{
    /// <summary>
    /// Generates the low-poly car bodies, wheels and tailpipes.
    ///
    /// The body is a stack of closed cross-sections along Z. A <see cref="CarProfile"/> describes one
    /// silhouette at a handful of points; those are interpolated to a fine grid so the shell is smooth
    /// and so the wheel arches can be carved out of the underside. Normals are smoothed, with hard
    /// creases inserted only where a real car has them by emitting a duplicate ring there so no normal
    /// averages across the edge.
    ///
    /// <para>There are ten profiles. <see cref="Fastback"/> is the player's default — a late-sixties
    /// American fastback: long hood, short deck, low roof, a roofline running unbroken from the cabin to
    /// the tail panel, and wide haunches over the rear wheels. Every one of the ten is both a car the
    /// player may drive and a shape they meet coming the other way, and each is measured against a real
    /// vehicle whose dimensions are quoted in its own doc comment.</para>
    ///
    /// <para>All ten share a wheelbase and a track, which is what keeps the wheel seating one problem
    /// rather than ten. They do <b>not</b> share a wheel: the tyre, the suspension travel and therefore
    /// the ride height are per profile, because an off-roader standing at a fastback's height on a
    /// fastback's tyre is not an off-roader. Everything that used to be arithmetic off those two shared
    /// numbers — the arch top, and the ground plane a station table is quoted against — is now derived
    /// per profile from <see cref="CarProfile.RideHeight"/> and <see cref="CarProfile.ArchTop"/>.</para>
    ///
    /// <para>The player's car is built at full detail (<see cref="BuildBody"/>); traffic runs the same
    /// loft at a fifth of the ring density with the grille, plates and exhausts left off
    /// (<see cref="BuildTrafficBody"/>).</para>
    ///
    /// Authoring-only, hence EditorTools.
    /// </summary>
    public static class CarMeshBuilder
    {
        public const int BodySubmesh = 0;
        public const int GlassSubmesh = 1;
        public const int HeadlightSubmesh = 2;
        public const int TaillightSubmesh = 3;
        public const int ChromeSubmesh = 4;
        public const int BodySubmeshCount = 5;

        public const int TyreSubmesh = 0;
        public const int RimSubmesh = 1;
        public const int WheelSubmeshCount = 2;

        /// <summary>
        /// How much bigger every car is than the shape it was authored as, in plan — length, width,
        /// track, wheelbase.
        ///
        /// <para><b>The tables below are not edited to grow the cars.</b> Every one of them is a real
        /// car measured against a real reference, argued about in its own comment — 4.74 m against a
        /// Mustang's 4.66, a 58° windscreen, 0.70 m of flat roof. Multiplying those numbers in place
        /// would destroy the only thing that makes them checkable. They are scaled once, at the single
        /// gate every profile passes through, so what is written stays what was measured.</para>
        /// </summary>
        public const float PlanScale = 1.25f;

        /// <summary>
        /// The same for everything vertical, and it is deliberately <b>not</b> <see cref="PlanScale"/>.
        ///
        /// <para>Growing in plan by a quarter and in height by 15 % is what makes the cars read as lower
        /// and wider rather than merely as bigger: the fastback's length-to-height goes from 3.31 to
        /// 3.61, where the '67 Mustang it is drawn from sits at 3.58.</para>
        ///
        /// <para><b>Everything vertical has to share this one number, and the reason is a centimetre.</b>
        /// The arch is cut at <c>WheelRadius − SuspensionRestLength + ArchGap</c> and the glass starts at
        /// the station table's <c>BeltY</c>. On the fastback those are 0.190 and 0.200 — ten millimetres
        /// apart. The coupé has twenty, the hatchback fifty. Scale the wheels without the body and the
        /// arch is cut through the side window on three of the ten cars; scale them together and the
        /// margin is preserved exactly, whatever the factor. That is why the wheels do not simply grow
        /// with the rest of the car.</para>
        /// </summary>
        public const float HeightScale = 1.15f;

        /// <summary>Scales a station table out of authored metres into built ones.</summary>
        private static Station[] Scaled(Station[] stations)
        {
            var scaled = new Station[stations.Length];
            for (int i = 0; i < stations.Length; i++)
            {
                Station station = stations[i];
                scaled[i] = new Station(
                    station.Z * PlanScale,
                    station.HalfWidth * PlanScale,
                    station.BeltY * HeightScale,
                    station.TopY * HeightScale,
                    station.TopHalfWidth * PlanScale,
                    station.SillY * HeightScale);
            }

            return scaled;
        }

        /// <summary>Scales a list of Z positions — crease lines, cabin bands — into built metres.</summary>
        private static float[] ScaledPlan(float[] values)
        {
            if (values == null)
            {
                return null;
            }

            var scaled = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                scaled[i] = values[i] * PlanScale;
            }

            return scaled;
        }

        /// <summary>
        /// Half the distance between the wheel centres. Must match the prefab's anchors. Set so the
        /// tyre stands a few centimetres proud of the fender — flush wheels read as recessed.
        ///
        /// <para>Scaled with the bodies rather than independently: the body is what covers the wheel, so
        /// widening one without the other either leaves the tyres standing outside the arches or sinks
        /// them into the flare until the car has no wheels at all.</para>
        /// </summary>
        public const float TrackHalfWidth = 0.99f * PlanScale;

        /// <summary>
        /// How far above the road the collider's underside sits, metres — <b>not</b> how far the
        /// bodywork does.
        ///
        /// <para><b>The wheels are raycasts, so the hull box is the only part of this car that can touch
        /// anything.</b> Measured honestly off the station table it reached down to the sill, which on
        /// every body but the off-roader put the front bumper between 3 and 8 centimetres above the road
        /// once the springs had taken the car's weight — under every kerb in the town, which are 0.10 to
        /// 0.17 m. Driving at one head-on planted the nose into its face and stopped the car dead, with
        /// nothing on screen to explain why: the tyre that should have climbed it has no collider, and
        /// the box that does had already hit.</para>
        ///
        /// <para>0.34 clears the tallest kerb by six centimetres even on the softest-sprung car at full
        /// static compression. Nothing else in the world is under 34 cm and solid — the guard rails
        /// start at 0.44 and buildings are buildings — so the box gives up nothing it was catching on
        /// purpose. It is a floor rather than a height: a body that already sits higher keeps its own.</para>
        ///
        /// <para>This is the collider only. The visible sill does not move, so a low car still looks
        /// low.</para>
        /// </summary>
        private const float ColliderGroundClearance = 0.34f * HeightScale;

        /// <summary>How far either side of a wheel centre the flare fades back to nothing, metres.</summary>
        private const float FlareReach = 0.75f * PlanScale;

        /// <summary>Distance of the wheel centres from the car's middle, along Z.</summary>
        public const float WheelBaseHalf = 1.35f * PlanScale;

        /// <summary>
        /// The one height the whole traffic pool is lifted by, metres.
        ///
        /// <para>The player's car reaches its ride height through its suspension: the wheel hangs at
        /// <c>-SuspensionRestLength</c> and the tyre reaches a radius below that. A traffic car has no
        /// suspension, so it is told the answer, and it is told the <i>same</i> answer for every shape —
        /// <c>TrafficDirector</c> lifts 96 transforms by one serialized float.</para>
        ///
        /// <para><b>Which is why a body whose own <see cref="CarProfile.RideHeight"/> differs from this
        /// has the difference baked into its traffic mesh</b> (see <see cref="BuildTrafficBody"/>). The
        /// alternative is a ride height per agent, which is a second number to keep in step for a
        /// difference of a few centimetres on a car seen at thirty metres through fog.</para>
        /// </summary>
        public const float TrafficRideHeight = 0.74f * HeightScale;

        /// <summary>
        /// How far apart the headlight beams sit either side of the centre line.
        ///
        /// <para>Shared rather than per profile, and that is not laziness: every one of these bodies is
        /// built around the same track, so their faces all land within a few centimetres of each other,
        /// and what the player sees of a beam is the pool of light on the road rather than its
        /// source.</para>
        /// </summary>
        private const float HeadlightHalfSpacing = 0.47f * PlanScale;

        /// <summary>
        /// Half-length of an arch opening along Z. Roughly the wheel radius plus a margin.
        ///
        /// 0.50 exactly, because <c>WheelBaseHalf - 0.50 = 0.85</c> lands on the cowl crease, where the
        /// arch contributes nothing anyway — so the front opening cannot ripple the base of the
        /// windscreen.
        /// </summary>
        private const float ArchHalfLength = 0.50f * PlanScale;

        /// <summary>
        /// One car's silhouette and its furniture, as a value.
        ///
        /// <para><b>This exists because a shape was five things and only one of them was the station
        /// table.</b> The glass bands, the crease positions and the Z the lamps are seated at were all
        /// bare literals scattered through the builder, every one of them a fact about the fastback
        /// rather than about cars. A second body type is wrong in all of them, and wrong quietly: put a
        /// van's lamps at the fastback's nose and they end up sealed inside the bodywork, which is a
        /// failure this file has already had once and only found because somebody rendered the town at
        /// night.</para>
        ///
        /// <para>The same argument, made a second time, is why the lamp <i>signatures</i>, the tailpipes
        /// and the running gear are here too. Ten bodies wearing one Mustang tail and one pair of pipes
        /// is ten cars that are the same car at ten lengths — the silhouette was already doing all the
        /// work of telling them apart, and a silhouette is the one thing a player cannot see when they
        /// are following a car rather than passing it.</para>
        ///
        /// <para>Note what is <i>not</i> here. <see cref="TrackHalfWidth"/> and
        /// <see cref="WheelBaseHalf"/> stay global: track is suspension geometry, this project tunes
        /// feel before beauty, and a wheelbase that varied per profile would buy variety no player can
        /// see from thirty metres through fog. The wheel itself <i>is</i> here, because a G-Klasse on a
        /// Mustang's tyre is not a G-Klasse — and everything a wheel decides is derived from it
        /// (<see cref="RideHeight"/>, <see cref="ArchTop"/>) rather than restated beside it.</para>
        /// </summary>
        public readonly struct CarProfile
        {
            /// <summary>Used in mesh and asset names, so it has to be a valid file name.</summary>
            public readonly string Name;

            public readonly Station[] Stations;

            /// <summary>
            /// Z positions that get a duplicated ring, so no normal averages across the edge.
            /// </summary>
            public readonly float[] CreaseZ;

            /// <summary>Z range over which the top surface is windscreen rather than bodywork.</summary>
            public readonly float WindscreenFrom;

            public readonly float WindscreenTo;

            /// <summary>And the backlight, where it lies along the roofline rather than standing up at
            /// the back of the car. Equal values switch it off — a pickup has no rear window, and a car
            /// with a vertical tailgate wants <see cref="TailGlassHalfWidth"/> instead.</summary>
            public readonly float RearWindowFrom;

            public readonly float RearWindowTo;

            /// <summary>
            /// The side glass, as {from, to} pairs along Z — one pair per window.
            ///
            /// <para><b>Pairs rather than one range, and this is the field that stopped every car in the
            /// garage looking like the same car.</b> A single band draws one unbroken dark stripe from
            /// the A-pillar to wherever it ends, which is a letterbox on any shape it is put on: the
            /// estate, the hatchback and the off-roader were separated by their rooflines alone, and a
            /// roofline is exactly what a thumbnail 300 px wide cannot resolve. Windows with pillars
            /// between them are legible at any size, because the eye counts them.</para>
            ///
            /// <para>A pillar has to be at least a couple of ring spacings wide to survive
            /// <see cref="StationStep"/>, and on a traffic body — built from key stations only — it has
            /// to have a key station at each of its edges or it is not sampled at all. Both are why the
            /// tables carry stations at window boundaries that the silhouette itself does not need.</para>
            /// </summary>
            public readonly float[] Cabin;

            /// <summary>
            /// Where the reduced body's lamp panels are seated.
            ///
            /// <b>On the caps, not near them.</b> A value a comfortable margin inside the shell puts
            /// every lamp on every car inside the bodywork, and nothing about that is visible by day.
            /// </summary>
            public readonly float NoseZ;

            public readonly float TailZ;

            // --- The tail.

            /// <summary>Which of the five tail-lamp layouts this car wears.</summary>
            public readonly TailLampStyle TailLamps;

            /// <summary>Lamp units per side. Ignored by <see cref="TailLampStyle.Strip"/>.</summary>
            public readonly int TailLampCount;

            /// <summary>
            /// Inboard and outboard edge of the lamp cluster, as a fraction of the tail's half-width, so
            /// a cluster follows the car it is on rather than being measured in metres against one of
            /// them.
            /// </summary>
            public readonly float TailLampInner;

            public readonly float TailLampOuter;

            /// <summary>Half the lamp's height, metres.</summary>
            public readonly float TailLampHalfHeight;

            /// <summary>
            /// How far below <see cref="LampHeight"/> the cluster is centred. Positive is down: a
            /// G-Klasse hangs its lamps off the top corners of the tailgate and a van sits them near the
            /// bumper, and that difference is most of what says which of the two is in front of you.
            /// </summary>
            public readonly float TailLampDrop;

            // --- The face.

            /// <summary>Which of the four front-end layouts this car wears.</summary>
            public readonly HeadLampStyle HeadLamps;

            /// <summary>
            /// Half-width of the grille opening as a fraction of the nose's own half-width. The Mustang's
            /// 0.689 is the default because it is the number this was before it was a field.
            /// </summary>
            public readonly float GrilleSpan;

            // --- The exhaust.

            /// <summary>
            /// Tailpipes. 0, 1, 2 or 4 — 1 exits on the left, as a single-pipe car's does, 2 is a
            /// symmetric pair and 4 is two pairs. Zero is a car with nothing worth drawing back there,
            /// and it also switches off its smoke emitters, because those are hung off these outlets.
            /// </summary>
            public readonly int ExhaustCount;

            public readonly float ExhaustRadius;

            /// <summary>Distance of the outer pipes from the centre line, metres.</summary>
            public readonly float ExhaustSpread;

            public readonly float ExhaustLength;

            /// <summary>
            /// Non-zero puts the pipe on the flank at this |x| instead of out of the tail panel, exiting
            /// just ahead of the rear wheel. An off-roader's does, and it is a detail worth the one
            /// field: a side pipe is visible from the side, which is the view a car in a garage row is
            /// seen in.
            /// </summary>
            public readonly float ExhaustSideExit;

            // --- Glass on the tail panel.

            /// <summary>
            /// Half-width of a window in the tail <i>cap</i>, as a fraction of the tail's half-width.
            /// <b>Zero means none</b>, which is what every body written before this field had.
            ///
            /// <para><b>The loft cannot express an upright rear window and this is the way round it.</b>
            /// <see cref="RearWindowFrom"/> puts glass on the top surface, so the more vertical a
            /// tailgate is the fewer centimetres of Z it occupies — an off-roader's 22°-off-vertical
            /// back panel came out as a nine-centimetre strip of roof, and the actual flat face behind
            /// the car is the cap, which <see cref="AddCap"/> writes into the body submesh and nothing
            /// else. A panel seated a few millimetres proud of that cap is a rear window, drawn the same
            /// way the grille is.</para>
            /// </summary>
            public readonly float TailGlassHalfWidth;

            /// <summary>Bottom and top of that window, in the body's local frame.</summary>
            public readonly float TailGlassBottom;

            public readonly float TailGlassTop;

            // --- Bolt-ons.

            /// <summary>
            /// Half the span of a bolted-on rear wing, metres. <b>Zero means no wing</b>, which is what
            /// every body written before this field had, so their meshes are untouched by it.
            ///
            /// <para>A wing is the one piece of a car that a station table cannot describe. The table
            /// lofts a closed cross-section per Z, and a wing is a plate standing in clear air above the
            /// deck with a gap under it — two surfaces at the same Z. The fastback's ducktail is what the
            /// table <i>can</i> do, and the difference between that and a nineties homologation wing is
            /// most of what tells those two cars apart at any distance.</para>
            /// </summary>
            public readonly float WingHalfSpan;

            /// <summary>Where the wing stands, along Z. Must sit ahead of <see cref="TailZ"/>.</summary>
            public readonly float WingZ;

            /// <summary>
            /// How far the blade floats above the deck at <see cref="WingZ"/> — the height of the stalks,
            /// measured from the top surface the table already describes there.
            /// </summary>
            public readonly float WingHeight;

            /// <summary>Radius of a spare wheel carried on the tailgate. Zero means none.</summary>
            public readonly float SpareWheelRadius;

            /// <summary>
            /// Z range over which the top surface is an <b>open load bed</b> rather than bodywork.
            /// Equal values mean none, which is every body but the pickup.
            ///
            /// <para><b>This is the one place the loft is cut open.</b> Everything else in this file is a
            /// closed cross-section per Z, and a pickup drawn that way has a lid on its bed — which is
            /// what this one had, and is why it read as an estate with a step in its roof. Inside the
            /// range the four top segments of every ring are left unstitched and
            /// <see cref="AddBed"/> drops a trough in through the hole: rail caps, inner walls, a floor
            /// and a wall at each end.</para>
            ///
            /// <para>The ends have to land on key stations, and the profile's table carries stations
            /// there for no other reason. A band is skinned or not by where its <i>midpoint</i> falls, so
            /// an opening that ends between two stations leaves a sliver of roof at one end and a sliver
            /// of hole at the other.</para>
            /// </summary>
            public readonly float BedFrom;

            public readonly float BedTo;

            /// <summary>
            /// Height of the flat part of the bed floor. It is only the flat part: the floor humps up
            /// over each wheel arch exactly as the underside does, because the arch is cut out of the
            /// same solid the bed is sunk into — and on a real pickup those humps are the most
            /// recognisable thing about the load bay.
            /// </summary>
            public readonly float BedFloorY;

            /// <summary>How thick the bed's side walls are, metres — the rail cap's width.</summary>
            public readonly float BedWallThickness;

            /// <summary>
            /// Indicator turrets standing on the front wing tops. An off-roader's, and they are there
            /// because they are the one piece of furniture on that car visible from the driver's seat.
            /// </summary>
            public readonly bool IndicatorTurrets;

            // --- Running gear.

            /// <summary>
            /// Rolling radius, metres. Mirrored into <c>VehicleConfig.WheelRadius</c> by
            /// <c>VehicleConfigPresets</c>, which reads it from here so the arch and the physics cannot
            /// disagree.
            ///
            /// <para>Changing it changes the gearing: <c>FinalDrive</c> has to move with it or the car
            /// silently gets a different top speed and a different set of shift points. See the note on
            /// <c>VehicleConfigPresets</c>.</para>
            /// </summary>
            public readonly float WheelRadius;

            /// <summary>Suspension travel, metres — and the other half of the ride height.</summary>
            public readonly float SuspensionRestLength;

            /// <summary>Tyre width, metres. Purely visual; nothing in the physics reads it.</summary>
            public readonly float TyreWidth;

            /// <summary>How far the widebody arches blister out beyond the flank, metres.</summary>
            public readonly float FlareWidth;

            /// <summary>
            /// How much daylight stands between the top of the tyre and the top of its arch, metres.
            ///
            /// <para><b>This is the number that says whether a car is lowered.</b> It used to be a
            /// constant 0.02 — which is what a car actually has when it is sitting on its bump stops, and
            /// so every one of the ten looked like a slammed sports car with its wheels jammed into the
            /// bodywork. A road car at rest has visible gap over the tyre, and an off-roader has a fist
            /// of it; that gap <i>is</i> the suspension travel, and it is the only place travel is
            /// visible on a parked car.</para>
            ///
            /// <para>It is a request rather than a promise. <see cref="BuildRing"/> caps every arch at
            /// <c>belt - 0.08</c> so an opening can never reach the beltline, and on a car with a low
            /// waist that cap bites first. <c>PrototypeSetup.ReportBodies</c> prints what each axle
            /// actually got, which is where to look when a car still reads as lowered.</para>
            /// </summary>
            public readonly float ArchGap;

            /// <summary>Which wheel this car wears.</summary>
            public readonly RimStyle Rim;

            /// <summary>
            /// Rim diameter as a fraction of the tyre's radius — so the rest of the tyre is sidewall.
            ///
            /// <para>0.58 is the fastback's and was the literal this replaces. Low profile reads as fast
            /// and expensive; a fat sidewall reads as a working vehicle, and on an off-roader it is also
            /// the truth about what the thing is for. It is the cheapest character a wheel has, and it
            /// costs no triangles at all.</para>
            /// </summary>
            public readonly float RimFraction;

            /// <summary>
            /// How far the body's local origin sits above the ground: the wheel centre hangs at
            /// <c>-SuspensionRestLength</c> and the tyre reaches a radius below that.
            ///
            /// <para>This is the frame every Y in a station table is quoted against, so it is worth
            /// reading before arguing with one of those numbers — the same table on a taller ride height
            /// describes a car with more ground clearance and nothing else.</para>
            /// </summary>
            public float RideHeight => SuspensionRestLength + WheelRadius;

            /// <summary>
            /// Top of the wheel arch openings. Sized so the wheel nearly fills the opening — an arch much
            /// larger than its wheel makes the car look like it is on the wrong rims.
            ///
            /// <para><b>Arithmetic, not a taste call.</b> The wheel centre hangs at
            /// <c>-SuspensionRestLength</c>, so the tread tops out at <c>WheelRadius - restLength</c>,
            /// and <see cref="ArchGap"/> is the daylight left above it. On the fastback this is the 0.16
            /// that used to be a file-level constant, and the reason it was one is that all ten bodies
            /// shared a wheel and a stance — which is exactly what stopped being true.</para>
            ///
            /// <para>Note this is a *request*, not the final height: <see cref="BuildRing"/> clamps the
            /// arch to <c>belt - 0.08</c> so the opening can never reach the beltline.</para>
            /// </summary>
            public float ArchTop => WheelRadius - SuspensionRestLength + ArchGap;

            public CarProfile(
                string name,
                Station[] stations,
                float[] creaseZ,
                float windscreenFrom,
                float windscreenTo,
                float rearWindowFrom,
                float rearWindowTo,
                float[] cabin,
                float noseZ,
                float tailZ,
                TailLampStyle tailLamps = TailLampStyle.Bars,
                int tailLampCount = 3,
                float tailLampInner = 0.1786f,
                float tailLampOuter = 0.7857f,
                float tailLampHalfHeight = 0.14f,
                float tailLampDrop = 0.06f,
                HeadLampStyle headLamps = HeadLampStyle.GrilleBar,
                float grilleSpan = 0.689f,
                int exhaustCount = 2,
                float exhaustRadius = 0.075f,
                float exhaustSpread = 0.42f,
                float exhaustLength = 0.38f,
                float exhaustSideExit = 0f,
                float tailGlassHalfWidth = 0f,
                float tailGlassBottom = 0f,
                float tailGlassTop = 0f,
                float wingHalfSpan = 0f,
                float wingZ = 0f,
                float wingHeight = 0f,
                float spareWheelRadius = 0f,
                bool indicatorTurrets = false,
                float bedFrom = 0f,
                float bedTo = 0f,
                float bedFloorY = 0f,
                float bedWallThickness = 0.10f,
                float wheelRadius = 0.44f,
                float suspensionRestLength = 0.30f,
                float tyreWidth = 0.34f,
                float flareWidth = 0.09f,
                float archGap = 0.02f,
                RimStyle rim = RimStyle.FiveSpoke,
                float rimFraction = 0.58f)
            {
                // Every dimension arrives here in the metres it was authored in — measured against a
                // real car, argued about in a comment — and leaves scaled. This is the one gate all ten
                // profiles pass through, which is why the scale is applied here rather than in ten
                // tables: the numbers below stay readable as the measurements they are.
                Name = name;
                Stations = Scaled(stations);
                CreaseZ = ScaledPlan(creaseZ);
                WindscreenFrom = windscreenFrom * PlanScale;
                WindscreenTo = windscreenTo * PlanScale;
                RearWindowFrom = rearWindowFrom * PlanScale;
                RearWindowTo = rearWindowTo * PlanScale;
                Cabin = ScaledPlan(cabin);
                NoseZ = noseZ * PlanScale;
                TailZ = tailZ * PlanScale;
                TailLamps = tailLamps;
                TailLampCount = tailLampCount;

                // Fractions of the face they sit on, so they follow the body without being touched.
                TailLampInner = tailLampInner;
                TailLampOuter = tailLampOuter;
                GrilleSpan = grilleSpan;
                TailGlassHalfWidth = tailGlassHalfWidth;
                RimFraction = rimFraction;

                TailLampHalfHeight = tailLampHalfHeight * HeightScale;
                TailLampDrop = tailLampDrop * HeightScale;
                HeadLamps = headLamps;
                ExhaustCount = exhaustCount;
                ExhaustRadius = exhaustRadius * PlanScale;
                ExhaustSpread = exhaustSpread * PlanScale;
                ExhaustLength = exhaustLength * PlanScale;
                ExhaustSideExit = exhaustSideExit * PlanScale;
                TailGlassBottom = tailGlassBottom * HeightScale;
                TailGlassTop = tailGlassTop * HeightScale;
                WingHalfSpan = wingHalfSpan * PlanScale;
                WingZ = wingZ * PlanScale;
                WingHeight = wingHeight * HeightScale;
                SpareWheelRadius = spareWheelRadius * HeightScale;
                IndicatorTurrets = indicatorTurrets;
                BedFrom = bedFrom * PlanScale;
                BedTo = bedTo * PlanScale;
                BedFloorY = bedFloorY * HeightScale;
                BedWallThickness = bedWallThickness * PlanScale;

                // The three that decide where the arch is cut against where the tyre stands. They share
                // HeightScale with the station table's Y for the reason given on that constant: the
                // margin between the arch top and the beltline is one centimetre on the fastback, and it
                // survives only if all of them move together.
                WheelRadius = wheelRadius * HeightScale;
                SuspensionRestLength = suspensionRestLength * HeightScale;
                ArchGap = archGap * HeightScale;

                TyreWidth = tyreWidth * PlanScale;
                FlareWidth = flareWidth * PlanScale;
                Rim = rim;
            }
        }

        /// <summary>
        /// The five tail-lamp layouts. Between them they cover every car in the garage, and the point of
        /// each one is the shape it cuts on a dark panel rather than its detail.
        /// </summary>
        public enum TailLampStyle
        {
            /// <summary>Vertical bars in a row — a '67 Mustang. The default, and what every body wore
            /// before there was a choice.</summary>
            Bars,

            /// <summary>One wide horizontal block per side, an eighties saloon's.</summary>
            Blocks,

            /// <summary>Round lenses, <see cref="CarProfile.TailLampCount"/> per side.</summary>
            Round,

            /// <summary>A tall upright lamp standing in the corner of the tailgate — estates and
            /// vans.</summary>
            Stack,

            /// <summary>One band straight across the tail, through the centre line.</summary>
            Strip,
        }

        /// <summary>
        /// The five wheels. A wheel is the one part of a car that is drawn four times and sits at eye
        /// level in the chase camera, so it is worth more per triangle than anything else on the body —
        /// and until this existed every car in the garage wore the fastback's five-spoke.
        /// </summary>
        public enum RimStyle
        {
            /// <summary>Five broad tapered spokes. A muscle car's, and the default.</summary>
            FiveSpoke,

            /// <summary>Ten thin ones. Reads as expensive, which is what a fast car wants.</summary>
            MultiSpoke,

            /// <summary>A plain dish with a ring of round holes — a working vehicle's steel wheel.</summary>
            Steel,

            /// <summary>Six chunky spokes on a deep dish, with a hub cap standing proud.</summary>
            OffRoad,

            /// <summary>Many shallow angled slats, an eighties alloy.</summary>
            Turbine,
        }

        /// <summary>The four front ends, and the same argument as <see cref="TailLampStyle"/>.</summary>
        public enum HeadLampStyle
        {
            /// <summary>Full-width grille with rectangular lamps set into its outer ends.</summary>
            GrilleBar,

            /// <summary>Round lamps either side of a narrow upright grille.</summary>
            Round,

            /// <summary>Slim wide lenses over a low, wide mouth.</summary>
            Slim,

            /// <summary>Square lamps stacked over an upright grille — an eighties three-box.</summary>
            Stacked,
        }

        public readonly struct Station
        {
            public readonly float Z;
            public readonly float HalfWidth;
            public readonly float BeltY;
            public readonly float TopY;
            public readonly float TopHalfWidth;

            /// <summary>
            /// Local floor height. Raised at the extreme ends so the nose and tail dome over instead
            /// of ending in a vertical slab, which is most of what made the old shape read as a block.
            /// </summary>
            public readonly float SillY;

            public Station(float z, float halfWidth, float beltY, float topY, float topHalfWidth, float sillY)
            {
                Z = z;
                HalfWidth = halfWidth;
                BeltY = beltY;
                TopY = topY;
                TopHalfWidth = topHalfWidth;
                SillY = sillY;
            }
        }

        /// <summary>
        /// The silhouette, tail to nose, measured against a 1967 Mustang fastback.
        ///
        /// <para><b>Every Y in this table is quoted below as a height above the ground</b>, which sits at
        /// -0.74: the wheel centre hangs at <c>-SuspensionRestLength</c> = -0.30 and the tyre radius is
        /// 0.44. That is the only frame in which these numbers can be argued with, because it is the one
        /// a photograph of a car is taken in — and measuring the body from its own floor instead is how
        /// the shape ended up 1.60 m tall while every ratio inside it looked correct.</para>
        ///
        /// <code>
        ///                       was    now    Mustang '67 fastback
        ///   length              4.88   4.74   4.66
        ///   width               2.08   2.08   1.80   (locked by TrackHalfWidth — see below)
        ///   height              1.60   1.43   1.30
        ///   wheelbase           2.70   2.70   2.74
        ///   front overhang      1.17   0.91   0.83
        ///   rear overhang       1.01   1.13   1.10
        ///   ground clearance    0.20   0.15   0.13
        ///   beltline            1.10   0.98   0.95
        ///   length / height     3.05   3.31   3.58
        ///   arch gap over tyre  0.10   0.02   ~0.02
        /// </code>
        ///
        /// <para>Four things were wrong and all four were proportion rather than surfacing. The car stood
        /// <b>1.60 m tall</b> against 1.30 — a length-to-height of 3.05 where a Mustang is 3.58, which is
        /// most of why it read as a bubble. Its <b>front overhang was 1.17 m</b> against 0.83 while the
        /// rear was short at 1.01, so the mass sat ahead of the front axle instead of behind it; a
        /// fastback is nose-short and tail-long, and getting that backwards makes any car look like it is
        /// leaning forward. It carried <b>0.20 m of ground clearance</b>. And the arch openings stood ten
        /// centimetres clear of their own tyres.</para>
        ///
        /// <para><b>Width is not in that list and cannot be.</b> 2.08 m against a real 1.80 is the one
        /// dimension this table does not own: the body has to cover the wheels, and the wheels are at
        /// <see cref="TrackHalfWidth"/>, which is suspension geometry. Narrowing the car means narrowing
        /// the track, which changes weight transfer and roll — and this project tunes feel before beauty.
        /// So the car stays a wide reading of a Mustang, and everything else moves to meet it.</para>
        ///
        /// <para>The shape it now describes: a dead-flat hood 1.25 m long at a constant 0.30, an upright
        /// face, a windscreen raked 58° from vertical, 0.70 m of flat roof, and then a single unbroken
        /// fall of 0.40 m over 1.70 m from the roof to the deck. That last line <i>is</i> the fastback.
        /// Flatten it and the same car is an estate; break it with a notch and it is a coupé.</para>
        /// </summary>
        private static readonly Station[] KeyStations =
        {
            // --- The tail. Long overhang, tiny deck, and a lip rather than a wing.
            //
            // The deck is 0.33 m from where the backlight lands to the tail panel. That is what makes a
            // fastback a fastback: on a notchback the roof stops and a boot begins, and the two are
            // different volumes. Here the roof simply keeps going until it runs out of car.
            //
            // TopY falls to 0.26 at the deck and kicks back up to 0.30 before cutting off — a four
            // centimetre lip. A '67 has barely any; the old table had 0.07 of upturn and it read as a
            // bolt-on spoiler on a car that should not have one.
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.48f, 0.84f, 0.18f, 0.23f, 0.58f, -0.47f),
            new Station(-2.42f, 0.88f, 0.20f, 0.27f, 0.64f, -0.52f),
            new Station(-2.32f, 0.92f, 0.22f, 0.30f, 0.68f, -0.55f),
            new Station(-2.15f, 0.97f, 0.24f, 0.26f, 0.70f, -0.59f),

            // --- The fastback slope and the rear haunch.
            //
            // One straight fall from the roof at -0.45 to the deck at -2.15, bowed out two centimetres
            // in the middle. BeltY rises to 0.31 over the rear axle and drops to 0.24 at the doors,
            // which is the haunch — and it is structural as well as styling, because BuildRing caps the
            // arch at belt - 0.08 and the belt is therefore what decides how large an opening can be.
            new Station(-1.90f, 1.00f, 0.27f, 0.33f, 0.70f, -0.59f),
            new Station(-1.60f, 1.02f, 0.30f, 0.42f, 0.68f, -0.59f),
            new Station(-1.35f, 1.04f, 0.31f, 0.49f, 0.66f, -0.59f),
            new Station(-1.15f, 1.02f, 0.29f, 0.55f, 0.64f, -0.59f),
            new Station(-0.90f, 0.99f, 0.26f, 0.61f, 0.62f, -0.59f),

            // --- The cabin. Roof flat from -0.45 to 0.25, which is 0.70 m of it.
            //
            // Roof at 0.66 over a beltline of 0.24 leaves 0.42 m of glass, and that ratio is deliberately
            // unchanged from before: seen from the chase camera, which looks down from behind, a shallow
            // greenhouse reads as a body pressed flat from above, and that view foreshortens height but
            // not width. What changed is that the whole cabin came down 0.22 m rather than the glass
            // getting thinner. TopHalfWidth 0.60 against a 0.97 body tucks the glasshouse in by well
            // over a third — a wide flat roof reads as flat however high it sits.
            new Station(-0.45f, 0.97f, 0.24f, 0.66f, 0.60f, -0.59f),
            new Station(0.25f, 0.97f, 0.24f, 0.66f, 0.60f, -0.59f),

            // --- The hood. Dead flat at 0.30 for 1.25 m, which is 1.04 m above the ground.
            //
            // Flat is the whole point. A hood that falls away towards the nose is a seventies boat, and
            // the previous table let TopY drift from 0.47 down to 0.19 over the last metre while the
            // sill rose 0.31 to meet it — the two together closed the nose into a snout. The face is
            // near-vertical now and the taper happens in plan, not in elevation.
            new Station(0.85f, 0.98f, 0.25f, 0.29f, 0.78f, -0.59f),
            new Station(1.15f, 1.01f, 0.27f, 0.30f, 0.80f, -0.59f),
            new Station(1.40f, 1.04f, 0.29f, 0.30f, 0.81f, -0.59f),
            new Station(1.70f, 1.02f, 0.27f, 0.30f, 0.81f, -0.59f),

            // --- The nose, at 2.26 rather than 2.52.
            //
            // 0.26 m came off the front overhang, which was 1.17 m against a real 0.83 and was the
            // largest single error in the old silhouette. A Mustang keeps its mass behind the front
            // axle; a long snout in front of it is a front-drive saloon whatever else is done to the
            // shape.
            //
            // Still not tapered to a point — a Mustang has a full, square-shouldered face, and a wedge
            // would be the wrong car. The cap is small enough not to read as a plate because the last
            // three stations pull the width in and dome the underside, not because the nose is pointed.
            new Station(1.95f, 1.00f, 0.25f, 0.30f, 0.79f, -0.58f),
            new Station(2.10f, 0.99f, 0.24f, 0.30f, 0.78f, -0.57f),
            new Station(2.20f, 0.96f, 0.22f, 0.29f, 0.75f, -0.55f),
            new Station(2.26f, 0.90f, 0.19f, 0.27f, 0.69f, -0.51f),
        };

        /// <summary>
        /// Z positions that get a duplicated ring, so no normal averages across the edge: the ducktail's
        /// leading edge, the deck edge, and both ends of the screen.
        ///
        /// The ducktail needs its crease or the upturn reads as a soft swelling in the deck rather than
        /// as a spoiler with a lip.
        /// </summary>
        private static readonly float[] CreaseZ = { -2.32f, -2.15f, 0.25f, 0.85f };

        /// <summary>
        /// The player's default car, and one of the ten shapes on the road.
        ///
        /// Every number in it is the one that was there before this became a profile — the glass bands,
        /// the creases and the lamp seating were literals inside the builder, and lifting them out is
        /// meant to change nothing about this car at all.
        /// </summary>
        public static readonly CarProfile Fastback = new CarProfile(
            "Fastback", KeyStations, CreaseZ,
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -1.55f, rearWindowTo: -0.45f,

            // One long door window, which is what a two-door fastback has. It is also the only cabin in
            // the garage that is still a single band, and deliberately: this car is the reference every
            // other table was measured against, so it keeps every number it had.
            cabin: new[] { -1.05f, 0.27f },
            noseZ: 2.27f, tailZ: -2.49f,
            tailLamps: TailLampStyle.Bars, tailLampCount: 3,
            headLamps: HeadLampStyle.GrilleBar,
            // The reference wheel: five broad spokes on a 0.58 rim.
            //
            // The two centimetres of arch gap this carried is a car sitting on its bump stops, and it
            // was every car in the garage until the gap became the profile's own. Nine is a road car at
            // rest, and the four centimetres of extra suspension travel that pay for it also lift the
            // whole car to 0.19 m of ground clearance. A '67 fastback is low; it is not slammed.
            rim: RimStyle.FiveSpoke, rimFraction: 0.58f, archGap: 0.09f,
            suspensionRestLength: 0.34f);
        /// <summary>
        /// An estate: the fastback's face and cabin, with the roof carried level to a raked tailgate.
        ///
        /// The cheapest useful variation there is, and the one that proves the profile is doing its job —
        /// it is the same car from the cowl forward and a different one behind the B-pillar, which is
        /// exactly what an estate is. The roof runs 0.75 rather than the fastback's 0.66, because an
        /// estate is taller over its load bay than a coupé is over its rear seats, and the ten
        /// centimetres that buys is most of what tells the two apart in silhouette.
        ///
        /// <para>Built: 4.68 m long, roof 1.53 m above the road. Note that is 4 cm more than
        /// <c>0.75 + 0.74</c> — <see cref="CrownFraction"/> bulges the top surface above its own edges,
        /// so a profile's built roof always stands a little proud of its table's <c>TopY</c>. Worth
        /// knowing before quoting a height from the numbers rather than from the build log.</para>
        /// </summary>
        private static readonly Station[] EstateStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.40f, 0.84f, 0.26f, 0.50f, 0.60f, -0.46f),
            new Station(-2.34f, 0.92f, 0.28f, 0.66f, 0.66f, -0.52f),
            new Station(-2.24f, 0.98f, 0.29f, 0.74f, 0.68f, -0.56f),

            // Level roof over the load bay, and the haunch still swells over the rear axle — an estate
            // that is a plain box from the door back reads as a delivery van rather than as a car.
            //
            // The stations at -2.12, -1.66, -1.48 and -0.62 are not shape, they are window edges: a
            // traffic body is lofted from key stations only, so a pillar with no station on it is a
            // pillar the reduced car does not have. See CarProfile.Cabin.
            new Station(-2.12f, 0.99f, 0.29f, 0.74f, 0.68f, -0.57f),
            new Station(-1.95f, 1.01f, 0.30f, 0.75f, 0.68f, -0.59f),
            new Station(-1.66f, 1.03f, 0.31f, 0.75f, 0.68f, -0.59f),
            new Station(-1.60f, 1.03f, 0.31f, 0.75f, 0.68f, -0.59f),
            new Station(-1.48f, 1.04f, 0.31f, 0.75f, 0.68f, -0.59f),
            new Station(-1.35f, 1.04f, 0.31f, 0.75f, 0.68f, -0.59f),
            new Station(-1.10f, 1.02f, 0.29f, 0.75f, 0.67f, -0.59f),
            new Station(-0.80f, 0.99f, 0.26f, 0.73f, 0.64f, -0.59f),
            new Station(-0.62f, 0.98f, 0.25f, 0.72f, 0.63f, -0.59f),

            // The cabin, and from here forward every number is the fastback's.
            new Station(-0.45f, 0.97f, 0.24f, 0.70f, 0.61f, -0.59f),
            new Station(0.25f, 0.97f, 0.24f, 0.68f, 0.60f, -0.59f),
            new Station(0.85f, 0.98f, 0.25f, 0.29f, 0.78f, -0.59f),
            new Station(1.15f, 1.01f, 0.27f, 0.30f, 0.80f, -0.59f),
            new Station(1.40f, 1.04f, 0.29f, 0.30f, 0.81f, -0.59f),
            new Station(1.70f, 1.02f, 0.27f, 0.30f, 0.81f, -0.59f),
            new Station(1.95f, 1.00f, 0.25f, 0.30f, 0.79f, -0.58f),
            new Station(2.10f, 0.99f, 0.24f, 0.30f, 0.78f, -0.57f),
            new Station(2.20f, 0.96f, 0.22f, 0.29f, 0.75f, -0.55f),
            new Station(2.26f, 0.90f, 0.19f, 0.27f, 0.69f, -0.51f),
        };

        /// <summary>
        /// A panel van. The one profile that differs in <b>height</b>, and therefore the one that does
        /// most of the work.
        ///
        /// <para>Shapes are read at thirty metres through fog, where a roofline is legible and a
        /// tailgate angle is not. A van standing 2.00 m tall against everything else's 1.4-1.5 is the
        /// only one of these five you can identify from the far side of the valley, which is why it is
        /// here and why it is worth the awkwardness of a beltline twice everyone else's.</para>
        ///
        /// <para>That high belt is not decoration either: side glass runs from the belt to the roof, so a
        /// van on a car's beltline has 0.85 m of window down each flank and reads as a minibus. At 0.62
        /// the glass is half a metre and the rest is slab.</para>
        /// </summary>
        private static readonly Station[] VanStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.55f, 0.94f, 0.58f, 1.10f, 0.80f, -0.46f),
            new Station(-2.48f, 1.00f, 0.60f, 1.18f, 0.86f, -0.52f),
            new Station(-2.40f, 1.03f, 0.61f, 1.21f, 0.88f, -0.56f),

            // Dead level for two and a half metres. A van is a box and the box is the point; the only
            // relief along here is the flare over the rear wheel, which FlareAt adds without the table
            // having to say anything.
            new Station(-1.90f, 1.05f, 0.62f, 1.21f, 0.88f, -0.59f),
            new Station(-1.35f, 1.06f, 0.62f, 1.21f, 0.88f, -0.59f),
            new Station(-0.60f, 1.05f, 0.62f, 1.21f, 0.88f, -0.59f),

            // Window edges. -0.10 to 0.36 is the middle side window and 0.52 to 1.30 the cab door; the
            // panel behind them stays a panel, which is what keeps this a van rather than a minibus.
            new Station(-0.10f, 1.04f, 0.61f, 1.21f, 0.88f, -0.59f),
            new Station(0.30f, 1.04f, 0.61f, 1.21f, 0.88f, -0.59f),
            new Station(0.36f, 1.04f, 0.61f, 1.21f, 0.88f, -0.59f),
            new Station(0.52f, 1.04f, 0.61f, 1.21f, 0.88f, -0.59f),
            new Station(1.05f, 1.03f, 0.60f, 1.21f, 0.87f, -0.59f),

            // Screen and stub bonnet. The screen falls 0.49 m over 0.32, which is 33° off vertical —
            // upright, as a cab-forward van's is. Rake it like the fastback's 58° and the whole nose has
            // to grow a metre to put it anywhere.
            new Station(1.30f, 1.02f, 0.59f, 1.21f, 0.85f, -0.59f),
            new Station(1.62f, 1.01f, 0.56f, 0.72f, 0.82f, -0.59f),
            new Station(1.85f, 1.02f, 0.52f, 0.50f, 0.82f, -0.59f),
            new Station(2.08f, 1.00f, 0.44f, 0.48f, 0.80f, -0.57f),
            new Station(2.22f, 0.96f, 0.36f, 0.45f, 0.76f, -0.54f),
            new Station(2.30f, 0.90f, 0.28f, 0.40f, 0.70f, -0.50f),
        };

        /// <summary>
        /// A pickup: the fastback's nose and a two-seat cab, then a genuinely open bed to the tail.
        ///
        /// <para>The step down from the cab roof at 0.64 to the bed rail at 0.50 happens over six
        /// centimetres, which is a vertical panel and needs a crease at both of its edges or the normals
        /// smear the back of the cab into the load bed and the whole thing reads as a melted estate.</para>
        ///
        /// <para><b>The rails stand 0.50 against the 0.36 they used to.</b> That is not styling either.
        /// The bed is a trough sunk into this table, its floor has to clear the rear wheel arch, and the
        /// arch on this car reaches 0.26 — so a rail at 0.36 leaves ten centimetres of bed, which is a
        /// tray rather than a load bay. At 0.50 it is 0.39 m deep between the arches and 0.23 m over
        /// them, and the humps that difference makes are the most recognisable thing in a pickup's
        /// bed.</para>
        ///
        /// <para>The stations at -2.42 and -0.78 are the ends of the opening. See
        /// <see cref="CarProfile.BedFrom"/> for why they have to be stations and not just numbers.</para>
        /// </summary>
        private static readonly Station[] PickupStations =
        {
            //           z       halfW  belt   top    topHalf sill
            // The tailgate, outside: it climbs from the tail cap to the rail line over 13 cm, and the
            // station at -2.42 is where the opening behind it starts.
            new Station(-2.55f, 0.92f, 0.28f, 0.33f, 0.86f, -0.48f),
            new Station(-2.48f, 0.98f, 0.30f, 0.40f, 0.90f, -0.53f),
            new Station(-2.42f, 1.00f, 0.31f, 0.48f, 0.92f, -0.55f),

            // The bed. TopHalfWidth runs close to HalfWidth along here on purpose: a load bed is flat
            // to its rails, unlike a roof, which tucks in — and here the rails are all that is left of
            // the top surface, because AddBed cuts the rest of it away.
            new Station(-2.35f, 1.02f, 0.32f, 0.50f, 0.94f, -0.57f),
            new Station(-1.90f, 1.04f, 0.33f, 0.51f, 0.96f, -0.59f),
            new Station(-1.35f, 1.05f, 0.34f, 0.52f, 0.97f, -0.59f),
            new Station(-0.90f, 1.03f, 0.33f, 0.51f, 0.95f, -0.59f),
            new Station(-0.78f, 1.01f, 0.32f, 0.50f, 0.92f, -0.59f),

            // The back of the cab.
            new Station(-0.72f, 0.99f, 0.28f, 0.64f, 0.60f, -0.59f),
            new Station(-0.45f, 0.98f, 0.26f, 0.70f, 0.60f, -0.59f),
            new Station(0.25f, 0.97f, 0.25f, 0.70f, 0.60f, -0.59f),

            // A front wing 6 cm above the fastback's, and a bonnet to match. Not styling: the arch has
            // to clear a 0.96 m wheel with a hand of travel over it, BuildRing caps every opening at
            // belt - 0.08, and BuildRing also refuses a top less than 0.05 above its own belt. So a
            // visible gap over the tyre buys itself a high wing and a high bonnet, which on a pickup is
            // what the real thing looks like anyway.
            new Station(0.85f, 0.98f, 0.30f, 0.36f, 0.78f, -0.59f),
            new Station(1.15f, 1.01f, 0.34f, 0.40f, 0.80f, -0.59f),
            new Station(1.40f, 1.04f, 0.35f, 0.40f, 0.81f, -0.59f),
            new Station(1.70f, 1.02f, 0.34f, 0.39f, 0.81f, -0.59f),
            new Station(1.95f, 1.00f, 0.31f, 0.38f, 0.79f, -0.58f),
            new Station(2.10f, 0.99f, 0.29f, 0.37f, 0.78f, -0.57f),
            new Station(2.20f, 0.96f, 0.26f, 0.34f, 0.75f, -0.55f),
            new Station(2.26f, 0.90f, 0.22f, 0.30f, 0.69f, -0.51f),
        };

        /// <summary>
        /// A small hatchback: 4.12 m against everyone else's 4.7-4.9, on the same wheelbase.
        ///
        /// <para>All of the 0.6 m comes off the overhangs, because the wheelbase is shared by every
        /// profile — which is what a small car actually is, and it is why this one works despite the
        /// constraint. Short overhangs on a fixed wheelbase read as a small car; a shortened wheelbase
        /// would read the same and would cost the arches, the flares and the wheel seating.</para>
        ///
        /// <para>The flanks stay wide at the axles (0.99) even though the car is narrow elsewhere: the
        /// wheels are at <see cref="TrackHalfWidth"/> like everything else, and a body that pulled in to
        /// match the small car's <i>look</i> would leave the tyres standing outside the arches.</para>
        /// </summary>
        private static readonly Station[] HatchbackStations =
        {
            //           z       halfW  belt   top    topHalf sill
            //
            // Sills 4 cm higher than the table this grew from, because the wheel came down: on a 0.40 m
            // tyre this car rides 0.69 m rather than 0.74, and the old floor would have left it with
            // ten centimetres of ground clearance and the stance of a slammed shopping trolley.
            new Station(-2.10f, 0.84f, 0.22f, 0.42f, 0.62f, -0.44f),
            new Station(-2.04f, 0.90f, 0.24f, 0.54f, 0.66f, -0.49f),
            new Station(-1.95f, 0.95f, 0.26f, 0.62f, 0.66f, -0.52f),

            new Station(-1.70f, 0.98f, 0.28f, 0.65f, 0.64f, -0.55f),
            new Station(-1.60f, 0.98f, 0.28f, 0.65f, 0.63f, -0.55f),
            new Station(-1.35f, 0.99f, 0.29f, 0.66f, 0.62f, -0.55f),
            new Station(-1.05f, 0.96f, 0.27f, 0.66f, 0.60f, -0.55f),
            new Station(-0.73f, 0.95f, 0.26f, 0.66f, 0.59f, -0.55f),
            new Station(-0.55f, 0.94f, 0.25f, 0.66f, 0.59f, -0.55f),
            new Station(-0.45f, 0.94f, 0.25f, 0.66f, 0.59f, -0.55f),
            new Station(0.25f, 0.94f, 0.25f, 0.66f, 0.59f, -0.55f),

            new Station(0.80f, 0.95f, 0.26f, 0.32f, 0.76f, -0.55f),
            new Station(1.10f, 0.98f, 0.27f, 0.33f, 0.78f, -0.55f),
            new Station(1.40f, 0.99f, 0.28f, 0.33f, 0.78f, -0.55f),
            new Station(1.70f, 0.97f, 0.27f, 0.32f, 0.77f, -0.54f),
            new Station(1.88f, 0.93f, 0.25f, 0.31f, 0.74f, -0.52f),
            new Station(2.00f, 0.87f, 0.21f, 0.28f, 0.68f, -0.47f),
        };

        /// <summary>
        /// The estate, measured against a Volvo 245.
        ///
        /// <para>Three side windows and a vertical tailgate with a real window in it. The lamps stand up
        /// the corners of that tailgate, which is the one thing about a boxy Swedish estate that anybody
        /// can draw from memory — and it is what tells this apart from the hatchback in the rear view,
        /// where the two rooflines are hidden behind each other.</para>
        /// </summary>
        public static readonly CarProfile Estate = new CarProfile(
            "Estate", EstateStations, new[] { -2.24f, 0.25f, 0.85f },
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -2.42f, rearWindowTo: -2.22f,
            cabin: new[] { -2.12f, -1.66f, -1.48f, -0.80f, -0.62f, 0.27f },
            noseZ: 2.27f, tailZ: -2.41f,
            tailLamps: TailLampStyle.Stack, tailLampCount: 1,
            tailLampInner: 0.60f, tailLampOuter: 0.86f,
            tailLampHalfHeight: 0.20f, tailLampDrop: -0.02f,
            headLamps: HeadLampStyle.Stacked, grilleSpan: 0.42f,
            exhaustCount: 1, exhaustRadius: 0.055f, exhaustSpread: 0.40f,
            tailGlassHalfWidth: 0.55f, tailGlassBottom: 0.30f, tailGlassTop: 0.48f,
            tyreWidth: 0.32f, flareWidth: 0.07f,
            archGap: 0.10f, rim: RimStyle.Turbine, rimFraction: 0.56f,
            suspensionRestLength: 0.34f);
        public static readonly CarProfile Van = new CarProfile(
            "Van", VanStations, new[] { -2.40f, 1.30f, 1.62f },
            windscreenFrom: 1.28f, windscreenTo: 1.64f,

            // No backlight along the roofline — the roof runs level to the very back of this one, so
            // there is no rear surface up there for glass to lie on. The window it does have stands
            // upright in the rear doors, which is what tailGlass is for.
            rearWindowFrom: 0f, rearWindowTo: 0f,
            cabin: new[] { -0.10f, 0.36f, 0.52f, 1.30f },
            noseZ: 2.31f, tailZ: -2.56f,
            tailLamps: TailLampStyle.Stack, tailLampCount: 1,
            tailLampInner: 0.62f, tailLampOuter: 0.90f,
            tailLampHalfHeight: 0.22f, tailLampDrop: 0.16f,
            headLamps: HeadLampStyle.Stacked, grilleSpan: 0.40f,
            exhaustCount: 1, exhaustRadius: 0.050f, exhaustSideExit: 0.58f,
            tailGlassHalfWidth: 0.52f, tailGlassBottom: 0.66f, tailGlassTop: 0.94f,
            wheelRadius: 0.46f, suspensionRestLength: 0.33f, flareWidth: 0.08f,
            // A steel wheel on a fat sidewall, and a hand of arch gap: this thing carries loads and
            // sits high on its springs when it is not carrying one.
            archGap: 0.12f, rim: RimStyle.Steel, rimFraction: 0.54f);
        public static readonly CarProfile Pickup = new CarProfile(
            "Pickup", PickupStations, new[] { -2.42f, -0.78f, -0.72f, 0.25f, 0.85f },
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -0.75f, rearWindowTo: -0.69f,
            cabin: new[] { -0.70f, 0.27f },
            noseZ: 2.27f, tailZ: -2.56f,

            // The load bay, 1.64 m of it. Floor at 0.20, which is 0.29 m under the rail — and only six
            // centimetres under the arch humps, which is the whole reason it is not lower. At 0.10 the
            // bed was 0.39 m deep between the arches and 0.23 over them, so it read as two wells with a
            // lump between them rather than as one load bay.
            bedFrom: -2.42f, bedTo: -0.78f, bedFloorY: 0.20f,
            tailLamps: TailLampStyle.Stack, tailLampCount: 1,
            tailLampInner: 0.60f, tailLampOuter: 0.92f,
            tailLampHalfHeight: 0.14f, tailLampDrop: 0.02f,
            headLamps: HeadLampStyle.GrilleBar, grilleSpan: 0.62f,
            exhaustCount: 1, exhaustRadius: 0.075f, exhaustSideExit: 0.60f,
            wheelRadius: 0.48f, suspensionRestLength: 0.36f,
            tyreWidth: 0.38f, flareWidth: 0.12f,
            archGap: 0.13f, rim: RimStyle.Steel, rimFraction: 0.52f);
        public static readonly CarProfile Hatchback = new CarProfile(
            "Hatchback", HatchbackStations, new[] { -1.95f, 0.25f, 0.80f },
            windscreenFrom: 0.25f, windscreenTo: 0.82f,
            rearWindowFrom: -2.06f, rearWindowTo: -1.93f,
            cabin: new[] { -1.60f, -0.73f, -0.55f, 0.27f },
            noseZ: 2.01f, tailZ: -2.11f,
            tailLamps: TailLampStyle.Stack, tailLampCount: 1,
            tailLampInner: 0.58f, tailLampOuter: 0.93f,
            tailLampHalfHeight: 0.17f, tailLampDrop: -0.02f,
            headLamps: HeadLampStyle.Stacked, grilleSpan: 0.38f,
            exhaustCount: 1, exhaustRadius: 0.045f, exhaustSpread: 0.34f,
            tailGlassHalfWidth: 0.56f, tailGlassBottom: 0.26f, tailGlassTop: 0.40f,
            wheelRadius: 0.40f, suspensionRestLength: 0.33f,
            tyreWidth: 0.28f, flareWidth: 0.06f,
            archGap: 0.10f, rim: RimStyle.FiveSpoke, rimFraction: 0.54f);
        /// <summary>
        /// A late-nineties Japanese performance coupé, measured against a Nissan Skyline R34 GT-R.
        ///
        /// <para><b>The opposite of the fastback in one line.</b> Where that car's roof falls unbroken
        /// into the tail, this one stops the roof, drops a near-vertical backlight and starts a separate
        /// boot deck — a notchback. The deck runs dead level for 60 cm at 1.10 m above the road, and it
        /// is there to carry the wing. Take the wing off and the shape still reads as a nineties coupé;
        /// take the deck away and there is nowhere to put the wing at all.</para>
        ///
        /// <code>
        ///                    built   R34 GT-R
        ///   length            4.62   4.60
        ///   height            1.37   1.36
        ///   wheelbase         2.70   2.665  (locked, see CarProfile)
        ///   front overhang    0.97   0.97
        ///   rear overhang     0.97   0.94
        ///   beltline          0.95   0.95
        /// </code>
        ///
        /// <para><b>TopHalfWidth 0.72 against the fastback's 0.60.</b> This is the number that stops it
        /// looking like a Mustang with a different roof. A muscle car tucks its glasshouse in hard; a
        /// GT-R's cabin is nearly as wide as its shoulders, and reading that wrong makes every square
        /// Japanese coupé look American.</para>
        /// </summary>
        private static readonly Station[] CoupeStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.31f, 0.86f, 0.16f, 0.30f, 0.72f, -0.48f),
            new Station(-2.26f, 0.92f, 0.18f, 0.34f, 0.80f, -0.53f),
            new Station(-2.20f, 0.97f, 0.20f, 0.37f, 0.86f, -0.56f),

            // The deck. Level, and the wing stands on it at -1.95.
            new Station(-1.95f, 1.01f, 0.23f, 0.36f, 0.90f, -0.59f),
            new Station(-1.60f, 1.04f, 0.28f, 0.36f, 0.90f, -0.59f),

            // Backlight, then 1.3 m of flat roof.
            new Station(-1.35f, 1.05f, 0.29f, 0.48f, 0.82f, -0.59f),
            new Station(-1.10f, 1.03f, 0.26f, 0.58f, 0.75f, -0.59f),
            new Station(-1.00f, 1.02f, 0.25f, 0.62f, 0.72f, -0.59f),
            new Station(-0.58f, 1.01f, 0.23f, 0.63f, 0.72f, -0.59f),
            new Station(-0.42f, 1.00f, 0.22f, 0.63f, 0.72f, -0.59f),
            new Station(-0.40f, 1.00f, 0.22f, 0.63f, 0.72f, -0.59f),
            new Station(0.30f, 1.00f, 0.22f, 0.62f, 0.72f, -0.59f),

            // The hood, and a wing crested over the front wheel: belt 0.29 at the axle against 0.26
            // at the cowl. That crest is worth 3 cm of arch opening, which is the whole gap over the
            // tyre on a car this low.
            new Station(0.90f, 1.02f, 0.26f, 0.32f, 0.84f, -0.59f),
            new Station(1.35f, 1.05f, 0.29f, 0.35f, 0.86f, -0.59f),
            new Station(1.75f, 1.04f, 0.27f, 0.33f, 0.86f, -0.59f),
            new Station(2.05f, 1.01f, 0.24f, 0.31f, 0.83f, -0.57f),
            new Station(2.20f, 0.97f, 0.21f, 0.29f, 0.79f, -0.54f),
            new Station(2.31f, 0.90f, 0.18f, 0.27f, 0.71f, -0.49f),
        };

        /// <summary>
        /// A nineties grand tourer, measured against a Toyota Supra A80.
        ///
        /// <para><b>The one car here with no straight lines in it.</b> Two creases, at the deck lip and
        /// the cowl, and nothing else — every other profile has four or five. The roof does not go flat
        /// anywhere: it rises to a crown at -0.20 and falls away in both directions, which is what makes
        /// the shape read as blown rather than folded. It is also the lowest car in the game at 1.26 m,
        /// nine centimetres under the fastback.</para>
        ///
        /// <para>Its wing stands 0.34 m off the deck, well clear of a roof that is lower than the
        /// coupé's — so the blade floats above the roofline instead of level with it. That gap is the
        /// difference between a homologation wing and a grand tourer's, and it is worth the one number
        /// it costs.</para>
        /// </summary>
        private static readonly Station[] LiftbackStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.27f, 0.86f, 0.14f, 0.22f, 0.64f, -0.47f),
            new Station(-2.20f, 0.92f, 0.16f, 0.28f, 0.70f, -0.52f),
            new Station(-2.10f, 0.97f, 0.18f, 0.31f, 0.74f, -0.56f),

            new Station(-1.90f, 1.01f, 0.20f, 0.30f, 0.76f, -0.59f),
            new Station(-1.65f, 1.04f, 0.24f, 0.33f, 0.74f, -0.59f),

            // The dome. No two adjacent stations share a TopY, on purpose.
            //
            // The beltline over the rear axle is 0.27 rather than 0.25, and that is not styling: this
            // car sits on 0.28 m of suspension travel, so its tyre tops out two centimetres higher than
            // it used to, and BuildRing caps the arch at belt - 0.08. Two centimetres of belt is what
            // buys the arch back.
            new Station(-1.35f, 1.05f, 0.27f, 0.40f, 0.70f, -0.59f),
            new Station(-1.15f, 1.04f, 0.24f, 0.44f, 0.67f, -0.59f),
            new Station(-1.05f, 1.03f, 0.23f, 0.46f, 0.66f, -0.59f),
            new Station(-0.70f, 1.01f, 0.20f, 0.49f, 0.63f, -0.59f),
            new Station(-0.56f, 1.00f, 0.19f, 0.49f, 0.63f, -0.59f),
            new Station(-0.40f, 1.00f, 0.19f, 0.49f, 0.62f, -0.59f),
            new Station(-0.20f, 0.99f, 0.18f, 0.49f, 0.62f, -0.59f),
            new Station(0.30f, 0.99f, 0.18f, 0.47f, 0.62f, -0.59f),

            // The front wing, crested over the axle. This car's belt was the lowest in the file and the
            // arch cap follows the belt, so its front tyre was cut *into* the bodywork — a negative gap,
            // which the build report now says out loud.
            new Station(0.80f, 1.01f, 0.24f, 0.30f, 0.80f, -0.59f),
            new Station(1.25f, 1.04f, 0.29f, 0.35f, 0.84f, -0.59f),
            new Station(1.70f, 1.03f, 0.27f, 0.33f, 0.84f, -0.58f),
            new Station(2.00f, 1.00f, 0.22f, 0.29f, 0.81f, -0.56f),
            new Station(2.15f, 0.95f, 0.17f, 0.25f, 0.76f, -0.53f),
            new Station(2.25f, 0.87f, 0.14f, 0.22f, 0.67f, -0.48f),
        };

        /// <summary>
        /// A compact eighties saloon, measured against a Mercedes 190E (W201).
        ///
        /// <para>A proper three-box: 0.85 m of flat roof, a backlight raked 64° from vertical, and then
        /// a boot deck that stands 1.18 m above the road — unusually high, and the single most
        /// recognisable thing about this car. The estate has a level roof to its tailgate and the coupé
        /// has a low deck under a wing; a tall short deck is neither, and it is what makes a small
        /// saloon look planted rather than stubby.</para>
        ///
        /// <para><b>HalfWidth drops to 0.93 along the doors and comes straight back to 1.04 at the rear
        /// axle.</b> A 190E is 1.68 m wide against this car's enforced 2.06, and pinching the waist is
        /// the only honest way to say so — but the body still has to cover wheels sitting at
        /// <see cref="TrackHalfWidth"/>, so the narrowing has to end before the arches do.</para>
        /// </summary>
        private static readonly Station[] SaloonStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.23f, 0.85f, 0.20f, 0.30f, 0.70f, -0.46f),
            new Station(-2.17f, 0.91f, 0.22f, 0.35f, 0.76f, -0.51f),
            new Station(-2.10f, 0.96f, 0.24f, 0.38f, 0.80f, -0.54f),

            // The deck: 1.12 m above the road, against the notchback's 1.07.
            //
            // It was 1.18, and that was 6 cm too many. The roof stood at 1.32, so the boot lid was
            // fourteen centimetres under the roofline where a real one of these is thirty-four under —
            // and seen from behind, which is the view a driver following this car has, the deck and the
            // roof read as one slab with a slot cut in it for the backlight. Five centimetres off the
            // deck and five onto the roof is most of the difference.
            new Station(-1.80f, 0.99f, 0.26f, 0.38f, 0.82f, -0.57f),
            new Station(-1.55f, 1.02f, 0.28f, 0.38f, 0.82f, -0.57f),

            // A backlight 42° off vertical over a quarter of a metre. The notchback lays the same
            // glass down to 67° over two and a half times the length — a saloon's rear window stands up
            // and a coupé's lies back, and that is the pair of shapes this table and that one are.
            new Station(-1.42f, 1.04f, 0.29f, 0.52f, 0.72f, -0.57f),
            new Station(-1.30f, 1.02f, 0.27f, 0.66f, 0.66f, -0.57f),

            // The waist, and the long flat roof over it. -0.50 and -0.32 are the B-pillar's edges.
            new Station(-0.60f, 0.95f, 0.23f, 0.66f, 0.64f, -0.57f),
            new Station(-0.50f, 0.95f, 0.23f, 0.66f, 0.64f, -0.57f),
            new Station(-0.32f, 0.94f, 0.23f, 0.66f, 0.64f, -0.57f),
            new Station(0.25f, 0.93f, 0.22f, 0.66f, 0.64f, -0.57f),

            new Station(0.85f, 0.96f, 0.25f, 0.32f, 0.78f, -0.57f),
            new Station(1.35f, 1.03f, 0.28f, 0.34f, 0.82f, -0.57f),
            new Station(1.70f, 1.01f, 0.26f, 0.32f, 0.81f, -0.56f),
            new Station(1.98f, 0.98f, 0.22f, 0.30f, 0.78f, -0.54f),
            new Station(2.13f, 0.94f, 0.20f, 0.29f, 0.74f, -0.51f),
            new Station(2.23f, 0.87f, 0.17f, 0.26f, 0.67f, -0.46f),
        };

        /// <summary>
        /// A compact eighties coupé, measured against a BMW E30.
        ///
        /// <para><b>This profile exists next to <see cref="Saloon"/> and has to earn it.</b> Two
        /// three-box saloons on one wheelbase is how a garage ends up with a row that looks like a
        /// duplicate, so the differences are deliberate and all three are visible in silhouette:</para>
        ///
        /// <list type="number">
        /// <item>13 cm shorter, all of it out of the overhangs, which is the only place a shared
        /// wheelbase leaves.</item>
        /// <item><b>The shark nose.</b> TopY falls from 0.30 at the cowl to 0.15 at the cap — 15 cm of
        /// forward droop where the saloon holds level to within two. The beltline drops with it. This
        /// one line is the whole car, and it is why the profile is worth having.</item>
        /// <item><b>A coupé's rear rather than a saloon's.</b> The backlight lies at 71° off vertical
        /// over 0.65 m against the saloon's 61° over 0.25, and the deck it lands on is 11 cm lower.
        /// This was the last thing added and the first thing that made the two legible apart at
        /// thumbnail size: length and nose alone were not enough, because a garage row is 300 px wide
        /// and a 13 cm difference in a 4.4 m car is nine of them.</item>
        /// <item>A beltline 3 cm lower under the same roof, so there is visibly more glass. An E30's
        /// pillars are thin and its greenhouse is deep; a 190E's is not.</item>
        /// </list>
        ///
        /// <para>It still keeps a boot deck, which is what holds it away from <see cref="Fastback"/> in
        /// the other direction — that car's roof falls unbroken to the tail and has no deck at all.</para>
        /// </summary>
        private static readonly Station[] NotchbackStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.17f, 0.84f, 0.17f, 0.25f, 0.68f, -0.46f),
            new Station(-2.11f, 0.90f, 0.19f, 0.30f, 0.74f, -0.51f),
            new Station(-2.04f, 0.95f, 0.21f, 0.33f, 0.78f, -0.54f),

            // A low deck, 1.07 m above the road against the saloon's 1.12, and 0.32 under its own roof
            // where it used to be 0.24 — see the saloon's table for why that number matters more than
            // either of the two it is made of.
            //
            // The beltline over the rear axle comes up two centimetres with it. That is arch clearance
            // rather than styling: BuildRing caps the opening at belt - 0.08, and this was the one axle
            // in the file where the cap, not the profile, was deciding how much tyre you could see.
            new Station(-1.75f, 0.99f, 0.25f, 0.33f, 0.80f, -0.57f),
            new Station(-1.50f, 1.02f, 0.27f, 0.33f, 0.80f, -0.57f),

            // 0.65 m of backlight, 71° off vertical. Long and lying down, where the saloon's is short
            // and upright — and the roof it lands on is a metre rather than a metre and a half.
            new Station(-1.20f, 1.04f, 0.28f, 0.48f, 0.72f, -0.57f),
            new Station(-1.18f, 1.04f, 0.28f, 0.49f, 0.72f, -0.57f),
            new Station(-0.85f, 1.01f, 0.25f, 0.61f, 0.64f, -0.57f),
            new Station(-0.46f, 0.97f, 0.22f, 0.64f, 0.63f, -0.57f),
            new Station(-0.30f, 0.95f, 0.21f, 0.65f, 0.62f, -0.57f),
            new Station(-0.28f, 0.95f, 0.21f, 0.65f, 0.62f, -0.57f),
            new Station(0.22f, 0.93f, 0.20f, 0.65f, 0.62f, -0.57f),

            // The cowl, and then the nose falls away from it: 0.30 down to 0.15 over 0.9 m, which is
            // the shark nose and the third of the three things separating this from the saloon.
            new Station(0.80f, 0.96f, 0.24f, 0.33f, 0.78f, -0.57f),
            new Station(1.25f, 1.02f, 0.28f, 0.34f, 0.82f, -0.57f),
            new Station(1.60f, 1.01f, 0.26f, 0.31f, 0.81f, -0.56f),
            new Station(1.90f, 0.97f, 0.21f, 0.26f, 0.77f, -0.54f),
            new Station(2.06f, 0.93f, 0.17f, 0.19f, 0.72f, -0.51f),
            new Station(2.16f, 0.86f, 0.14f, 0.15f, 0.65f, -0.45f),
        };

        /// <summary>
        /// A boxy off-roader, measured against a Mercedes G-Klasse (W463).
        ///
        /// <code>
        ///                       was    now    G-Klasse W463
        ///   length              4.66   4.68   4.66
        ///   width               2.08   2.10   1.76   (locked by TrackHalfWidth)
        ///   height              1.91   1.91   1.93
        ///   wheelbase           2.70   2.70   2.85   (locked)
        ///   rocker height       0.24   0.40   0.45
        ///   bonnet height       1.36   1.25   1.15
        ///   beltline            1.22   1.31   1.34
        ///   wheel diameter      0.88   0.96   0.78
        ///   gap over the tyre   0.02   0.15   ~0.14
        /// </code>
        ///
        /// <para><b>Four things were wrong with the shape this replaces and none of them was the
        /// roofline.</b> It stood on the fastback's tyre at the fastback's ride height, so it had a car's
        /// ground clearance under a truck's body. Its bonnet sat 1.36 m up, twenty centimetres over the
        /// real thing, and ate the windscreen from below until the glasshouse was the only thing left in
        /// the side view. Its side glass was one unbroken 3.05 m band with no pillar in it. And the flat
        /// panel you actually see when you are behind one was solid paint, because the loft can only put
        /// glass on a top surface and this vehicle's tailgate has none.</para>
        ///
        /// <para>What separates it from the van, which is the other tall box here, is now five things
        /// rather than three: no tumblehome at all (TopHalfWidth 0.95 against a HalfWidth of 1.00, where
        /// the van runs 0.88 against 1.05); an upright screen, 19° off vertical against the van's 33°,
        /// over a real 1.24 m bonnet where the van has a stub; the raised stance; three separate side
        /// windows; and a spare wheel bolted to the back of it.</para>
        ///
        /// <para>The sill sits 0.44 m above the road, which is a rocker height rather than a ground
        /// clearance — and it is still well under <see cref="CarProfile.ArchTop"/>, so
        /// <see cref="BottomAt"/> goes on cutting the arches. Raise a sill above its own arch top and the
        /// openings quietly stop being cut and the wheels turn into castors under a slab.</para>
        /// </summary>
        private static readonly Station[] OffroaderStations =
        {
            //           z       halfW  belt   top    topHalf sill
            //
            // Quoted against a ground plane at -0.82: this car rides on a 0.48 m tyre over 0.34 m of
            // travel, which is 8 cm more than everything else in the garage. Every height below is that
            // much further off the road than the same number on a fastback.
            new Station(-2.33f, 0.92f, 0.45f, 1.03f, 0.86f, -0.34f),
            new Station(-2.28f, 0.97f, 0.48f, 1.08f, 0.92f, -0.38f),
            new Station(-2.22f, 0.99f, 0.49f, 1.09f, 0.94f, -0.42f),

            // Dead level and dead vertical for three metres, broken only by the pillars: the stations at
            // -2.15, -1.55, -1.38, -0.70 and -0.52 are window edges rather than shape. Three side
            // windows with real pillars between them is what stops a box this size reading as a minibus,
            // and it is the single biggest thing separating this from the van.
            new Station(-2.15f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(-1.85f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(-1.55f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(-1.38f, 1.01f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(-0.70f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(-0.52f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(0.10f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(0.62f, 1.00f, 0.49f, 1.09f, 0.95f, -0.42f),
            new Station(0.86f, 1.00f, 0.48f, 1.09f, 0.95f, -0.42f),

            // The screen, and then a metre and a quarter of dead flat bonnet 1.25 m above the road.
            //
            // The bonnet used to sit at 0.62, which was 1.36 m up — higher than the real vehicle's by
            // twenty centimetres, and it ate the screen from below until the glasshouse was the only
            // thing left in the side view. Dropping it is most of why this now reads as a vehicle with a
            // nose rather than as a bus with a step in it.
            //
            // It came back up 3 cm when the arches did, and that is the trade this profile balances:
            // 15 cm of daylight over a tyre needs the arch at 0.29, the belt 0.08 above that, and the
            // bonnet 0.05 above *that*. Wanting a bigger wheel as well is what pushes the nose back to
            // where it started — which is why the wheel is 0.48 and not the 0.52 it briefly was.
            new Station(1.10f, 1.00f, 0.38f, 0.43f, 0.96f, -0.42f),
            new Station(1.45f, 1.01f, 0.37f, 0.42f, 0.97f, -0.42f),
            new Station(1.95f, 1.01f, 0.36f, 0.41f, 0.97f, -0.42f),
            new Station(2.22f, 0.99f, 0.34f, 0.39f, 0.95f, -0.40f),
            new Station(2.34f, 0.94f, 0.30f, 0.35f, 0.89f, -0.36f),
        };

        public static readonly CarProfile Coupe = new CarProfile(
            "Coupe", CoupeStations, new[] { -2.20f, -1.60f, 0.30f, 0.90f },
            windscreenFrom: 0.30f, windscreenTo: 0.90f,
            rearWindowFrom: -1.60f, rearWindowTo: -1.00f,
            cabin: new[] { -1.10f, -0.58f, -0.42f, 0.32f },
            noseZ: 2.32f, tailZ: -2.32f,

            // Four round lenses, two a side. It is the only tail in the garage that is round at all, and
            // on a car this shape it is the single detail that names it.
            tailLamps: TailLampStyle.Round, tailLampCount: 2,
            tailLampInner: 0.30f, tailLampOuter: 0.92f, tailLampHalfHeight: 0.13f,
            headLamps: HeadLampStyle.Slim, grilleSpan: 0.60f,

            // Two fat pipes close in to the centre line. Wide-set pipes read as an American V8; a pair
            // tucked either side of the diffuser is what a Japanese turbo car of this era wears.
            exhaustCount: 2, exhaustRadius: 0.105f, exhaustSpread: 0.30f, exhaustLength: 0.34f,

            // Level with the roof, which is where a homologation wing sits: the deck is at 0.36 and the
            // roof at 0.58, so 0.26 of stalk puts the blade a couple of centimetres proud of it.
            wingHalfSpan: 0.80f, wingZ: -2.02f, wingHeight: 0.26f,
            suspensionRestLength: 0.30f, tyreWidth: 0.38f, flareWidth: 0.11f,
            // Ten thin spokes on a 0.74 rim. Low profile is most of what makes a wheel read as
            // expensive, and this is the car in the garage that should.
            // Five centimetres, not the nine the road cars got. This one is allowed to look lowered —
            // it and the liftback are the only bodies in the garage that should.
            archGap: 0.05f, rim: RimStyle.MultiSpoke, rimFraction: 0.74f);
        public static readonly CarProfile Liftback = new CarProfile(
            "Liftback", LiftbackStations, new[] { -2.10f, 0.80f },
            windscreenFrom: 0.30f, windscreenTo: 0.80f,
            rearWindowFrom: -2.05f, rearWindowTo: -1.10f,
            cabin: new[] { -1.15f, -0.56f, -0.40f, 0.32f },
            noseZ: 2.26f, tailZ: -2.28f,
            tailLamps: TailLampStyle.Round, tailLampCount: 2,
            tailLampInner: 0.26f, tailLampOuter: 0.90f, tailLampHalfHeight: 0.12f,
            headLamps: HeadLampStyle.Slim, grilleSpan: 0.58f,

            // One pipe, and it is enormous: 0.27 m across the mouth. The car this is measured against is
            // remembered for exactly two things and this is the second of them — a single cannon under
            // the left of the bumper, big enough to read as a hole from a hundred metres back.
            exhaustCount: 1, exhaustRadius: 0.135f, exhaustSpread: 0.30f, exhaustLength: 0.34f,

            // Above the roofline rather than level with it — deck 0.30 plus 0.34 puts the blade at 0.64
            // over a roof of 0.49.
            wingHalfSpan: 0.82f, wingZ: -1.98f, wingHeight: 0.34f,
            suspensionRestLength: 0.31f, tyreWidth: 0.38f, flareWidth: 0.11f,
            archGap: 0.05f, rim: RimStyle.MultiSpoke, rimFraction: 0.72f);
        public static readonly CarProfile Saloon = new CarProfile(
            "Saloon", SaloonStations, new[] { -2.10f, -1.55f, 0.25f, 0.85f },
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -1.55f, rearWindowTo: -1.30f,
            cabin: new[] { -1.30f, -0.50f, -0.32f, 0.27f },
            noseZ: 2.24f, tailZ: -2.24f,

            // Two blocks stacked into one deep unit each side, which is how the ribbed lens of an
            // eighties Mercedes reads at any distance you can see the car from.
            tailLamps: TailLampStyle.Blocks, tailLampCount: 2,
            tailLampInner: 0.24f, tailLampOuter: 0.93f,
            tailLampHalfHeight: 0.13f, tailLampDrop: 0.02f,
            headLamps: HeadLampStyle.Stacked, grilleSpan: 0.36f,
            exhaustCount: 1, exhaustRadius: 0.055f, exhaustSpread: 0.36f,
            wheelRadius: 0.42f, suspensionRestLength: 0.32f,
            tyreWidth: 0.30f, flareWidth: 0.05f,
            archGap: 0.08f, rim: RimStyle.Turbine, rimFraction: 0.58f);
        public static readonly CarProfile Notchback = new CarProfile(
            "Notchback", NotchbackStations, new[] { -2.04f, -1.50f, 0.22f, 0.80f },
            windscreenFrom: 0.22f, windscreenTo: 0.80f,
            rearWindowFrom: -1.50f, rearWindowTo: -0.85f,
            cabin: new[] { -1.18f, -0.46f, -0.28f, 0.24f },
            noseZ: 2.17f, tailZ: -2.18f,

            // One wide shallow block each side, running almost to the plate. Against the saloon's deep
            // ribbed pair this is the flat eighties coupé tail, and the two are legible apart from
            // behind — which is where the shark nose, the thing that actually separates them, cannot be
            // seen at all.
            tailLamps: TailLampStyle.Blocks, tailLampCount: 1,
            tailLampInner: 0.22f, tailLampOuter: 0.94f,
            tailLampHalfHeight: 0.115f, tailLampDrop: 0.02f,
            headLamps: HeadLampStyle.Round, grilleSpan: 0.26f,

            // Twin pipes almost touching under the centre of the bumper.
            exhaustCount: 2, exhaustRadius: 0.050f, exhaustSpread: 0.13f,
            wheelRadius: 0.42f, suspensionRestLength: 0.32f,
            tyreWidth: 0.30f, flareWidth: 0.05f,
            archGap: 0.08f, rim: RimStyle.Turbine, rimFraction: 0.60f);
        public static readonly CarProfile Offroader = new CarProfile(
            "Offroader", OffroaderStations, new[] { -2.22f, 0.86f, 1.10f, 2.22f },
            windscreenFrom: 0.86f, windscreenTo: 1.10f,

            // No roofline backlight. The tailgate stands 22° off vertical, so the band the top surface
            // spends on it was nine centimetres of Z — a sliver of roof that this file used to call a
            // rear window, and that from behind was a solid painted panel. The window is on the cap now;
            // see tailGlass below.
            rearWindowFrom: 0f, rearWindowTo: 0f,

            // Three windows and three pillars. The old single band ran 3.05 m from the D-pillar to the
            // windscreen without a break in it, and that one number is why the shape read as a minibus
            // however correct its roofline was.
            cabin: new[] { -2.15f, -1.55f, -1.38f, -0.70f, -0.52f, 0.62f },
            noseZ: 2.34f, tailZ: -2.34f,

            // Square lamps hung high in the corners of the tailgate, clear of both the window and the
            // spare wheel — which between them own the middle of that panel.
            tailLamps: TailLampStyle.Blocks, tailLampCount: 1,
            tailLampInner: 0.78f, tailLampOuter: 0.94f,
            tailLampHalfHeight: 0.15f, tailLampDrop: -0.42f,
            headLamps: HeadLampStyle.Round, grilleSpan: 0.30f,

            // Out of the side, ahead of the rear wheel, where this vehicle's is.
            exhaustCount: 1, exhaustRadius: 0.075f, exhaustSideExit: 0.62f,

            // A real upright rear window, which is the whole reason tailGlass exists.
            tailGlassHalfWidth: 0.74f, tailGlassBottom: 0.72f, tailGlassTop: 1.00f,
            spareWheelRadius: 0.42f, indicatorTurrets: true,
            wheelRadius: 0.48f, suspensionRestLength: 0.34f,
            tyreWidth: 0.42f, flareWidth: 0.15f,
            // Fifteen centimetres of daylight over the tyre, which is six times the fastback's and is
            // the whole difference between a vehicle with suspension travel and a lowered one. On a
            // fat-sidewalled 0.50 rim with a locking hub standing proud of it.
            archGap: 0.15f, rim: RimStyle.OffRoad, rimFraction: 0.50f);
        /// <summary>
        /// The shapes ambient traffic is built from.
        ///
        /// <para>The fastback is in here as well as being the player's car, and that is deliberate: the
        /// player's own model appearing in traffic is what makes it one car among many rather than the
        /// only one of its kind in the world.</para>
        ///
        /// <para><b>Order is load-bearing.</b> It is the order of <c>VehicleConfigPresets.All</c>, of the
        /// garage menu and of the saved <c>PlayerChoices.Car</c> index. New bodies go on the end, so a
        /// returning player keeps the car they had rather than being handed whatever slid into its
        /// slot.</para>
        /// </summary>
        public static readonly CarProfile[] TrafficProfiles =
        {
            Fastback, Estate, Van, Pickup, Hatchback,
            Coupe, Liftback, Saloon, Notchback, Offroader,
        };

        /// <summary>
        /// Every shape the player may drive. The same ten, and deliberately the same array contents as
        /// <see cref="TrafficProfiles"/>: a car the player can pick is a car they should also meet coming
        /// the other way, and two lists would drift.
        ///
        /// <para>The traffic bodies are the reduced build, so the wings do not come with them —
        /// <c>details</c> is false in <see cref="BuildTrafficBody"/>. A coupé passing at 30 m is a
        /// silhouette, and 96 agents paying for a spoiler nobody resolves is the sort of cost this
        /// project's budget section exists to refuse.</para>
        /// </summary>
        public static readonly CarProfile[] PlayerProfiles = TrafficProfiles;

        /// <summary>
        /// The profile of that name, or the fastback if there is no such thing.
        ///
        /// <para>Exists so <c>VehicleConfigPresets</c> can read a car's wheel and suspension travel off
        /// the same value the mesh is lofted from. Those two numbers decide both what the car looks like
        /// (the arch top, the ground plane the station table is quoted against) and how it drives (the
        /// gearing, the spring solve), and holding them in two places is holding them in one place and a
        /// copy of it.</para>
        /// </summary>
        public static CarProfile ProfileByName(string name)
        {
            for (int i = 0; i < PlayerProfiles.Length; i++)
            {
                if (PlayerProfiles[i].Name == name)
                {
                    return PlayerProfiles[i];
                }
            }

            return Fastback;
        }

        /// <summary>
        /// The box that encloses the bodywork, for the collider.
        ///
        /// <para><b>From the station table, not from <c>mesh.bounds</c>.</b> The finished mesh carries
        /// the detail pass as well as the shell — tailpipe mouths 3 cm behind the tail cap, a lamp bar
        /// 2 cm behind that, a grille 2 cm ahead of the nose — so a box fitted to it is some 8 cm longer
        /// than the car and catches on scenery the bodywork never touched. Worse, it would be a box that
        /// changes when somebody moves a lamp. The stations are the bodywork; the trim is not.</para>
        ///
        /// <para>The four numbers come from the same expressions <see cref="BuildRing"/> builds the ring
        /// out of, so the box tracks the shell by construction rather than by somebody re-deriving it
        /// after a reshape. For the fastback it produced centre (0, 0.05, −0.11) and size
        /// (2.26, 1.28, 4.74) — exactly the literals it replaced — until the cars were scaled up;
        /// the same expressions now return those figures times
        /// <see cref="PlanScale"/> and <see cref="HeightScale"/>, because the station table they read
        /// is already scaled. <c>PrototypeSetup.ReportBodies</c> prints them every rebuild.</para>
        /// </summary>
        public static Bounds HullBounds(in CarProfile profile)
        {
            Station[] stations = profile.Stations;

            float minZ = stations[0].Z;
            float maxZ = stations[stations.Length - 1].Z;

            float halfX = 0f;
            float minY = float.MaxValue;
            float maxY = float.MinValue;

            for (int i = 0; i < stations.Length; i++)
            {
                Station station = stations[i];

                // The flare is added at every station rather than only over the axles: it is a maximum,
                // and a box has to hold the widest part of the car wherever that falls.
                halfX = Mathf.Max(halfX, station.HalfWidth + profile.FlareWidth);

                // Both clamps BuildRing applies to the underside, so an arch that pushes the floor up
                // cannot make the box shallower than the body actually is.
                minY = Mathf.Min(minY, Mathf.Min(station.SillY, station.BeltY - 0.08f));

                // The crowned apex, not the flat top: the roof bulges above TopY by a fraction of its
                // own width, and a box cut at TopY clips it.
                float top = Mathf.Max(station.TopY, station.BeltY + 0.05f);
                maxY = Mathf.Max(maxY, top + station.TopHalfWidth * CrownFraction);
            }

            // ...and then lifted off the road, which is the one place this box is deliberately not the
            // shape of the car. See ColliderGroundClearance: a bumper at sill height cannot mount a kerb,
            // and a raycast wheel has nothing else to offer it.
            minY = Mathf.Max(minY, ColliderGroundClearance - profile.RideHeight);

            return new Bounds(
                new Vector3(0f, (maxY + minY) * 0.5f, (maxZ + minZ) * 0.5f),
                new Vector3(halfX * 2f, maxY - minY, maxZ - minZ));
        }

        /// <summary>
        /// Where the two headlight beams are emitted from — the <see cref="Light"/> objects, not the
        /// lamp lenses in the mesh.
        ///
        /// <para>Set back a hand's width behind the nose and a hand's width above the lens, which is
        /// where the existing pair sat as literals: for the fastback this returns
        /// (±0.47, 0.20, 2.05) unchanged. It is deliberately not the lens position. A spot cone starting
        /// exactly on the lens clips its own bodywork on the first bump, and the beam the player sees is
        /// the pool of light on the road rather than the source.</para>
        /// </summary>
        public static Vector3[] HeadlightSeats(in CarProfile profile)
        {
            float z = profile.NoseZ - 0.22f;
            float y = LampHeight(profile, profile.NoseZ) + 0.18f;

            return new[]
            {
                new Vector3(HeadlightHalfSpacing, y, z),
                new Vector3(-HeadlightHalfSpacing, y, z),
            };
        }

        /// <summary>
        /// Local position of each tailpipe mouth, for hanging the smoke emitters on and for drawing the
        /// pipes themselves.
        ///
        /// <para>Just clear of the tail cap and just above the sill at that end. For the fastback that
        /// is (±0.42, −0.44, −2.52), the literals this replaces; for a van, whose tail is 7 cm further
        /// back and whose floor is elsewhere, it is not — which is the point.</para>
        ///
        /// <para><b>Count and spread are the car's own.</b> A single fat pipe, a symmetric pair and two
        /// pairs are three different cars from behind, and behind is the only view of a car a player who
        /// is following one ever gets. An odd count exits on the left, because that is the side a
        /// one-pipe car's does.</para>
        ///
        /// <para>A side exit is placed on the flank just ahead of the rear arch rather than on the tail
        /// panel. It still points backwards — the emitters are turned to face −Z whatever they are hung
        /// on — so nothing downstream has to know which kind of pipe it got.</para>
        /// </summary>
        public static Vector3[] ExhaustOutletsFor(in CarProfile profile)
        {
            if (profile.ExhaustCount <= 0)
            {
                return System.Array.Empty<Vector3>();
            }

            float sill = profile.Stations[0].SillY;

            if (profile.ExhaustSideExit > 0.0001f)
            {
                // Ahead of the rear wheel and just under the floor, not level with it: a pipe seated on
                // the sill line is inside the bodywork at that Z, which is a tailpipe nobody can see.
                return new[]
                {
                    new Vector3(-profile.ExhaustSideExit, sill - 0.03f, -WheelBaseHalf + 0.62f),
                };
            }

            float z = profile.TailZ - 0.03f;
            float y = sill + 0.03f;
            float spread = profile.ExhaustSpread;

            switch (profile.ExhaustCount)
            {
                case 1:
                    return new[] { new Vector3(-spread, y, z) };

                case 4:
                    // Two pairs, the inner one tucked against the outer with a pipe's width between
                    // them. Four pipes strung evenly across the tail is a bus, not a quad exhaust.
                    float inner = spread - profile.ExhaustRadius * 2.4f;
                    return new[]
                    {
                        new Vector3(spread, y, z),
                        new Vector3(inner, y, z),
                        new Vector3(-inner, y, z),
                        new Vector3(-spread, y, z),
                    };

                default:
                    return new[]
                    {
                        new Vector3(spread, y, z),
                        new Vector3(-spread, y, z),
                    };
            }
        }

        /// <summary>Spacing of the interpolated cross-sections.</summary>
        private const float StationStep = 0.13f;

        /// <summary>
        /// How much the top surface bulges above its edges, as a fraction of the roof half-width.
        /// Without this the roof and hood are dead-flat plates spanning the full width, and no amount
        /// of rounding elsewhere stops the car reading as a box.
        /// </summary>
        private const float CrownFraction = 0.055f;

        private const int KeyPointCount = 17;

        /// <summary>
        /// How many points each of a cross-section's seventeen key segments is drawn with, for the
        /// player's car. Ambient traffic runs the same loft at 1 — see <see cref="BuildTrafficBody"/>.
        /// </summary>
        private const int RingSubdivisions = 3;

        /// <summary>Ring segments forming the top surface — roof, hood, windscreen, rear window.</summary>
        private static readonly HashSet<int> TopKeySegments = new HashSet<int> { 6, 7, 8, 9 };

        /// <summary>
        /// Ring segments forming the side-window band, between the beltline and the roof rail.
        /// Segments 5 and 10 are the rails themselves and stay body colour — including them let the
        /// glass climb over the edge of the roof.
        /// </summary>
        private static readonly HashSet<int> FlankKeySegments = new HashSet<int> { 3, 4, 11, 12 };

        /// <summary>
        /// Builds a player car body at full detail. Five submeshes — see the Submesh constants for the
        /// order.
        ///
        /// <para><b>Never pass <c>usedSubmeshes</c> from here.</b> Leaving it null is what keeps the five
        /// slots uncompacted and in constant order, which is the only reason
        /// <c>VehicleLights.headlightMaterialIndex</c> and <c>taillightMaterialIndex</c> can stay the
        /// literal 2 and 3 for every body. Compacting a player body would put the lamps on the wrong
        /// material, and nothing would say so until dark.</para>
        /// </summary>
        public static Mesh BuildBody(in CarProfile profile, string meshName)
        {
            return BuildShell(profile, BuildFineStations(profile), RingSubdivisions, true, meshName);
        }

        /// <summary>
        /// The same car at a fraction of the cost, for ambient traffic.
        ///
        /// <para>The <i>silhouette</i> is what is being reused, and it is the only thing worth reusing: an
        /// ambient car is read at thirty metres through fog while the player is looking at the road, and
        /// at that size a shape that agrees with the player's own car matters and the panel gaps on it do
        /// not. So it runs off the same station table with the ring subdivided once instead of three
        /// times and the front, rear and exhaust details left off — about a tenth of the triangles, and a
        /// pool of fourteen costs less than one player car did.</para>
        ///
        /// <para>Key stations only, rather than the interpolated fine grid: the table's own entries are
        /// where the shape actually changes, and everything between them is a straight loft.</para>
        /// </summary>
        /// <param name="usedSubmeshes">
        /// Filled with the submesh constants that survived, in the order they ended up in the mesh.
        ///
        /// <para>Traffic bodies are compacted and the player's car is not, so a traffic car's slot 1 is
        /// no longer <see cref="GlassSubmesh"/> and its lamps are no longer at 2 and 3. Anything wiring
        /// materials or the day/night swap onto one has to look its slots up in here rather than know
        /// them — which is what the town's buildings have always done, for exactly this reason.</para>
        /// </param>
        public static Mesh BuildTrafficBody(in CarProfile profile, List<int> usedSubmeshes)
        {
            var stations = new List<Station>(profile.Stations);

            // Glass folded into the body, and the empty slot dropped. A traffic car is seen in motion at
            // fifty metres and up, where a separate glass material buys a reflection nobody resolves —
            // and at ninety-six cars it was buying ninety-six draw calls of it. The player's own car
            // keeps its glass: that one sits in the chase camera.
            // Lowered — or raised — by however far this car's own ride height differs from the one the
            // director lifts every agent by. See TrafficRideHeight: the pool gets one number, so the
            // difference has to live in the mesh, and putting it here means the BoxCollider taken from
            // mesh.bounds follows it without being told.
            return BuildShell(profile, stations, 1, false, $"TrafficCarMesh_{profile.Name}",
                mergeGlass: true, usedSubmeshes: usedSubmeshes,
                verticalOffset: TrafficRideHeight - profile.RideHeight);
        }

        /// <summary>
        /// The lofted shell, at whatever ring density and level of detail the caller asks for.
        ///
        /// <paramref name="ringSubdivisions"/> is how many points each of the seventeen key segments of a
        /// cross-section is drawn with, so it multiplies the vertex count of every ring;
        /// <paramref name="details"/> covers the grille, lights, plates and exhausts, which are a fixed
        /// cost per car rather than a per-ring one.
        /// </summary>
        private static Mesh BuildShell(
            in CarProfile profile,
            List<Station> stations,
            int ringSubdivisions,
            bool details,
            string meshName,
            bool mergeGlass = false,
            List<int> usedSubmeshes = null,
            float verticalOffset = 0f)
        {
            int ringVertexCount = KeyPointCount * ringSubdivisions;

            var vertices = new List<Vector3>(2048);
            var submeshTriangles = new List<int>[BodySubmeshCount];
            for (int i = 0; i < BodySubmeshCount; i++)
            {
                submeshTriangles[i] = new List<int>(1024);
            }

            var rowZ = new List<float>();
            var rowStart = new List<int>();
            var rowIsDuplicate = new List<bool>();

            for (int i = 0; i < stations.Count; i++)
            {
                Station station = stations[i];
                bool interior = i > 0 && i < stations.Count - 1;
                int copies = interior && IsCrease(profile, station.Z) ? 2 : 1;

                Vector3[] ring = BuildRing(profile, station, ringSubdivisions);
                for (int copy = 0; copy < copies; copy++)
                {
                    rowZ.Add(station.Z);
                    rowStart.Add(vertices.Count);
                    rowIsDuplicate.Add(copy > 0);
                    vertices.AddRange(ring);
                }
            }

            for (int row = 0; row < rowStart.Count - 1; row++)
            {
                // Skip the zero-thickness gap between the two copies of a crease station.
                if (rowIsDuplicate[row + 1])
                {
                    continue;
                }

                float midZ = (rowZ[row] + rowZ[row + 1]) * 0.5f;
                int backStart = rowStart[row];
                int frontStart = rowStart[row + 1];

                // Over an open load bed the ring's top surface is not drawn at all — AddBed puts a
                // trough in through the hole it leaves. See CarProfile.BedFrom.
                bool openTop = midZ > profile.BedFrom && midZ < profile.BedTo;

                for (int i = 0; i < ringVertexCount; i++)
                {
                    int keySegment = i / ringSubdivisions;
                    if (openTop && TopKeySegments.Contains(keySegment))
                    {
                        continue;
                    }

                    int next = (i + 1) % ringVertexCount;
                    int submesh = ResolveSubmesh(profile, midZ, keySegment);
                    List<int> triangles = submeshTriangles[submesh];

                    int a = backStart + i;
                    int b = backStart + next;
                    int c = frontStart + i;
                    int d = frontStart + next;

                    // Winding chosen so faces point outwards; RecalculateNormals depends on it.
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);

                    triangles.Add(b);
                    triangles.Add(d);
                    triangles.Add(c);
                }
            }

            // Caps get their own vertices so the tail and nose edges stay crisp.
            AddCap(vertices, submeshTriangles[BodySubmesh],
                BuildRing(profile, stations[0], ringSubdivisions), facingForward: false);
            AddCap(vertices, submeshTriangles[BodySubmesh],
                BuildRing(profile, stations[stations.Count - 1], ringSubdivisions), facingForward: true);

            // Before the detail pass, not inside it: the reduced traffic body skips details, and a
            // pickup with the lid cut off and nothing put back is a car you can see straight through.
            AddBed(profile, vertices, submeshTriangles, ringSubdivisions);

            if (details)
            {
                AddFrontDetails(profile, vertices, submeshTriangles);
                AddRearDetails(profile, vertices, submeshTriangles);
                AddRearWindow(profile, vertices, submeshTriangles);
                AddWing(profile, vertices, submeshTriangles);
                AddSpareWheel(profile, vertices, submeshTriangles);
                AddIndicatorTurrets(profile, vertices, submeshTriangles);

                // Long enough to run back under the tail rather than poke out of it like a peg.
                Vector3[] outlets = ExhaustOutletsFor(profile);
                for (int i = 0; i < outlets.Length; i++)
                {
                    AddTube(vertices, submeshTriangles[ChromeSubmesh], outlets[i],
                        profile.ExhaustRadius, profile.ExhaustLength, 12);
                }
            }
            else
            {
                // The lamps alone, as flat panels. Without them a traffic car has no headlight or
                // taillight submesh at all, and the day-and-night material swap that lights the town has
                // nothing on it to swap — which after dark is a car with no lights on.
                AddTrafficLamps(profile, vertices, submeshTriangles);
            }

            if (mergeGlass)
            {
                submeshTriangles[BodySubmesh].AddRange(submeshTriangles[GlassSubmesh]);
                submeshTriangles[GlassSubmesh].Clear();
            }

            if (Mathf.Abs(verticalOffset) > 0.0001f)
            {
                for (int i = 0; i < vertices.Count; i++)
                {
                    Vector3 vertex = vertices[i];
                    vertex.y += verticalOffset;
                    vertices[i] = vertex;
                }
            }

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);

            if (usedSubmeshes == null)
            {
                // Uncompacted, which is what the player's car wants: its submesh constants are read
                // directly by everything that dresses it, and an empty slot costs one draw call on one
                // object.
                mesh.subMeshCount = BodySubmeshCount;
                for (int i = 0; i < BodySubmeshCount; i++)
                {
                    mesh.SetTriangles(submeshTriangles[i], i);
                }
            }
            else
            {
                usedSubmeshes.Clear();
                for (int i = 0; i < BodySubmeshCount; i++)
                {
                    if (submeshTriangles[i].Count > 0)
                    {
                        usedSubmeshes.Add(i);
                    }
                }

                mesh.subMeshCount = usedSubmeshes.Count;
                for (int slot = 0; slot < usedSubmeshes.Count; slot++)
                {
                    mesh.SetTriangles(submeshTriangles[usedSubmeshes[slot]], slot);
                }
            }

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Interpolates the key stations onto a fine grid. Key Z values are always included, so the
        /// crease positions land exactly on a station and the windscreen keeps its hard edge.
        /// </summary>
        private static List<Station> BuildFineStations(in CarProfile profile)
        {
            Station[] key = profile.Stations;
            var fine = new List<Station>(64);

            for (int gap = 0; gap < key.Length - 1; gap++)
            {
                Station from = key[gap];
                Station to = key[gap + 1];

                float span = to.Z - from.Z;
                int steps = Mathf.Max(1, Mathf.CeilToInt(span / StationStep));

                for (int step = 0; step < steps; step++)
                {
                    float t = step / (float)steps;
                    fine.Add(new Station(
                        Mathf.Lerp(from.Z, to.Z, t),
                        Mathf.Lerp(from.HalfWidth, to.HalfWidth, t),
                        Mathf.Lerp(from.BeltY, to.BeltY, t),
                        Mathf.Lerp(from.TopY, to.TopY, t),
                        Mathf.Lerp(from.TopHalfWidth, to.TopHalfWidth, t),
                        Mathf.Lerp(from.SillY, to.SillY, t)));
                }
            }

            fine.Add(key[key.Length - 1]);
            return fine;
        }

        private static bool IsCrease(in CarProfile profile, float z)
        {
            for (int i = 0; i < profile.CreaseZ.Length; i++)
            {
                if (Mathf.Abs(profile.CreaseZ[i] - z) < 0.001f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// How far the bodywork blisters outwards at a given Z — the widebody arches.
        ///
        /// Deliberately a function of position applied to the flank points of the cross-section rather
        /// than new bolt-on geometry. The ring stays a closed loop and the ring-to-ring stitching is
        /// untouched, so there is no seam to leak and nothing to keep in step with the shell. Widening
        /// <see cref="Station.HalfWidth"/> instead would have been simpler still, but that moves the
        /// whole flank from sill to roof rail and gives a car that is uniformly fatter — not one with
        /// arches over its wheels.
        ///
        /// SmoothStep rather than a linear ramp: a cone has a visible kink where it meets the flank,
        /// and the point of a flare is that it swells.
        /// </summary>
        private static float FlareAt(in CarProfile profile, float z)
        {
            float flare = 0f;

            for (int side = -1; side <= 1; side += 2)
            {
                float distance = Mathf.Abs(z - side * WheelBaseHalf) / FlareReach;
                if (distance >= 1f)
                {
                    continue;
                }

                flare = Mathf.Max(flare, profile.FlareWidth * Mathf.SmoothStep(0f, 1f, 1f - distance));
            }

            return flare;
        }

        /// <summary>
        /// Underside height at a given Z. Rises into a roughly circular arch over each wheel — that
        /// opening is what stops the wheels looking like castors bolted under a slab.
        /// </summary>
        private static float BottomAt(in CarProfile profile, float z, float sillY)
        {
            float bottom = sillY;

            for (int side = -1; side <= 1; side += 2)
            {
                float distance = Mathf.Abs(z - side * WheelBaseHalf) / ArchHalfLength;
                if (distance >= 1f)
                {
                    continue;
                }

                float arch = Mathf.Lerp(sillY, profile.ArchTop, Mathf.Sqrt(1f - distance * distance));
                bottom = Mathf.Max(bottom, arch);
            }

            return bottom;
        }

        /// <summary>
        /// Fourteen control points, smoothed into a closed loop. Pairs of points sit close together at
        /// the belt line and the shoulder, which tightens those corners — a muscle car needs a crisp
        /// beltline, not an egg. Reuses the road's Catmull-Rom rather than a second copy of it.
        /// </summary>
        /// <summary>
        /// Four flat lamp panels and four wheels, for the reduced-detail body.
        ///
        /// <para>Panels rather than the recessed units the player's car gets: at the distance this is
        /// seen the lamps are two bright marks in the dark and their job is to exist at all.</para>
        ///
        /// <para><b>The Z values are on the caps, not near them.</b> The first version put the headlights
        /// at 2.42 and the tail lamps at -2.30, which are both a comfortable margin <i>inside</i> a shell
        /// that ends at 2.52 and -2.36 — so every lamp on every car was sealed in the bodywork, and at
        /// night the pool drove around with no lights at all. Nothing about that is visible by day, which
        /// is why it survived until there was a night render to look at.</para>
        ///
        /// <para>The wheels go in the chrome submesh, which the reduced body otherwise leaves empty
        /// because the exhausts are part of the detail pass. That is worth doing deliberately: it means
        /// four wheels cost no extra draw call, only a different material in a slot that was being
        /// submitted anyway.</para>
        /// </summary>
        private static void AddTrafficLamps(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            float noseZ = profile.NoseZ;
            float tailZ = profile.TailZ;

            // Seated against each profile's own beltline rather than at a fixed height: a van's face is a
            // metre taller than a fastback's, and lamps at the fastback's 0.02 would sit at its knees.
            float noseY = LampHeight(profile, noseZ);
            float tailY = LampHeight(profile, tailZ);

            // The tail panel keeps the cluster its detailed twin wears, reduced to one bright mark per
            // side: a G-Klasse's lamps sit high in the corners and a van's sit low, and at this distance
            // that difference survives where the lamp's shape does not.
            float tailX = (profile.TailLampInner + profile.TailLampOuter) * 0.5f
                          * HalfWidthAt(profile, tailZ);

            AddLampPanel(vertices, submeshTriangles[HeadlightSubmesh], 0.30f, noseY, noseZ, 0.18f, 0.08f);
            AddLampPanel(vertices, submeshTriangles[HeadlightSubmesh], -0.30f, noseY, noseZ, 0.18f, 0.08f);
            AddLampPanel(vertices, submeshTriangles[TaillightSubmesh],
                tailX, tailY - profile.TailLampDrop, tailZ, 0.22f, 0.09f);
            AddLampPanel(vertices, submeshTriangles[TaillightSubmesh],
                -tailX, tailY - profile.TailLampDrop, tailZ, 0.22f, 0.09f);

            // Seated so the tyre touches the road once the mesh has taken its ride-height offset: the
            // ground is at -RideHeight in this frame, whatever the director's single lift is.
            float radius = profile.WheelRadius;
            float centreY = -profile.RideHeight + radius;

            // At TrackHalfWidth, not inboard of it. That constant is set so a tyre stands proud of the
            // widebody flare — put the wheel a hand's width further in, as this first did, and the
            // bodywork swallows it and the car has no wheels at all.
            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? -TrackHalfWidth : TrackHalfWidth;
                float z = (i & 2) == 0 ? -WheelBaseHalf : WheelBaseHalf;

                AddTrafficWheel(vertices, submeshTriangles[ChromeSubmesh],
                    new Vector3(x, centreY, z), radius, profile.TyreWidth * 0.38f, 8);
            }
        }

        /// <summary>
        /// Where a lamp panel sits vertically: a hand below the beltline at that end of the car.
        ///
        /// <para>Derived from the profile's own stations rather than given as a number per profile, so a
        /// body type cannot be described without its lamps following. The alternative — one height for
        /// every shape — puts a van's headlights at the height of a fastback's, which on a face a metre
        /// taller is somewhere around its front axle.</para>
        ///
        /// <para>The beltline is the right datum rather than the roof or the floor because it is the one
        /// line every one of these shapes has in the same place relative to its lamps: below the glass,
        /// above the wheel. On the fastback this lands within a centimetre of the fixed 0.02 it
        /// replaced.</para>
        /// </summary>
        private static float LampHeight(in CarProfile profile, float z)
        {
            const float belowBelt = 0.17f;

            return BeltAt(profile, z) - belowBelt;
        }

        /// <summary>
        /// The beltline at a given Z, interpolated between stations. Split out of
        /// <see cref="LampHeight"/> because the arch cap needs it too — <see cref="BuildRing"/> clamps
        /// every opening to <c>belt - 0.08</c>, so on a car with a low waist the beltline, not
        /// <see cref="CarProfile.ArchGap"/>, is what decides how much of the tyre you can see.
        /// </summary>
        private static float BeltAt(in CarProfile profile, float z)
        {
            Station[] stations = profile.Stations;

            // Off either end, the nearest station is the answer: a lamp is seated on a cap, and the cap
            // is the last station.
            if (z <= stations[0].Z)
            {
                return stations[0].BeltY;
            }

            for (int i = 1; i < stations.Length; i++)
            {
                if (z > stations[i].Z)
                {
                    continue;
                }

                float span = stations[i].Z - stations[i - 1].Z;
                float t = span > 0.0001f ? (z - stations[i - 1].Z) / span : 0f;

                return Mathf.Lerp(stations[i - 1].BeltY, stations[i].BeltY, t);
            }

            return stations[stations.Length - 1].BeltY;
        }

        /// <summary>
        /// The daylight the build actually left between the top of a tyre and the top of its arch,
        /// metres — <see cref="CarProfile.ArchGap"/> after <see cref="BuildRing"/>'s beltline cap has
        /// had its say.
        ///
        /// <para>Reported per axle by <c>PrototypeSetup.ReportBodies</c>, because the request and the
        /// result can differ by a lot and the difference is invisible in the table: a profile that asks
        /// for 11 cm of gap under a beltline that only allows 6 still reads as a lowered car, and
        /// nothing about the numbers it was written with would say so.</para>
        /// </summary>
        public static float ArchClearanceAt(in CarProfile profile, float z)
        {
            float opening = Mathf.Min(profile.ArchTop, BeltAt(profile, z) - 0.08f);

            return opening - (profile.WheelRadius - profile.SuspensionRestLength);
        }

        /// <summary>
        /// Half the bodywork's width at a given Z, interpolated between stations the same way
        /// <see cref="LampHeight"/> interpolates the beltline. Off either end the nearest station
        /// answers, because that is the cap a grille or a lamp bar is being seated on.
        ///
        /// <para>Without the flare: this is used to size panels on the faces, and the flares are over the
        /// axles, a metre away from both of them.</para>
        /// </summary>
        private static float HalfWidthAt(in CarProfile profile, float z)
        {
            Station[] stations = profile.Stations;

            if (z <= stations[0].Z)
            {
                return stations[0].HalfWidth;
            }

            for (int i = 1; i < stations.Length; i++)
            {
                if (z > stations[i].Z)
                {
                    continue;
                }

                float span = stations[i].Z - stations[i - 1].Z;
                float t = span > 0.0001f ? (z - stations[i - 1].Z) / span : 0f;

                return Mathf.Lerp(stations[i - 1].HalfWidth, stations[i].HalfWidth, t);
            }

            return stations[stations.Length - 1].HalfWidth;
        }

        /// <summary>
        /// One wheel as a closed n-gon prism about the X axis.
        ///
        /// Eight sides, which is two more than a boulder gets and enough that the silhouette reads as
        /// round at the distance a car in the next street is seen from. No rim, no tread, no hub: the
        /// whole point of putting these in the chrome slot is that they are one flat dark colour.
        /// </summary>
        private static void AddTrafficWheel(
            List<Vector3> vertices, List<int> triangles, Vector3 centre, float radius, float halfWidth,
            int sides)
        {
            int start = vertices.Count;

            for (int i = 0; i < sides; i++)
            {
                float angle = i * (Mathf.PI * 2f / sides);
                float y = centre.y + Mathf.Sin(angle) * radius;
                float z = centre.z + Mathf.Cos(angle) * radius;

                vertices.Add(new Vector3(centre.x - halfWidth, y, z));
                vertices.Add(new Vector3(centre.x + halfWidth, y, z));
            }

            for (int i = 0; i < sides; i++)
            {
                int a = start + i * 2;
                int b = start + i * 2 + 1;
                int c = start + ((i + 1) % sides) * 2;
                int d = start + ((i + 1) % sides) * 2 + 1;

                triangles.Add(a);
                triangles.Add(c);
                triangles.Add(b);

                triangles.Add(b);
                triangles.Add(c);
                triangles.Add(d);
            }

            // Both discs, so a wheel seen from in front of the car is not a hollow tube.
            int inner = vertices.Count;
            vertices.Add(new Vector3(centre.x - halfWidth, centre.y, centre.z));
            vertices.Add(new Vector3(centre.x + halfWidth, centre.y, centre.z));

            for (int i = 0; i < sides; i++)
            {
                int a = start + i * 2;
                int c = start + ((i + 1) % sides) * 2;

                triangles.Add(inner);
                triangles.Add(a);
                triangles.Add(c);

                triangles.Add(inner + 1);
                triangles.Add(c + 1);
                triangles.Add(a + 1);
            }
        }

        /// <summary>One lamp: a quad facing along the car's own axis, at the Z it is given.</summary>
        private static void AddLampPanel(
            List<Vector3> vertices,
            List<int> triangles,
            float x,
            float y,
            float z,
            float halfWidth,
            float halfHeight)
        {
            int start = vertices.Count;
            bool front = z > 0f;

            vertices.Add(new Vector3(x - halfWidth, y - halfHeight, z));
            vertices.Add(new Vector3(x + halfWidth, y - halfHeight, z));
            vertices.Add(new Vector3(x + halfWidth, y + halfHeight, z));
            vertices.Add(new Vector3(x - halfWidth, y + halfHeight, z));

            // Wound so the panel looks the way the end of the car it is on does. The corners run
            // anticlockwise in the XY plane, which gives a +Z normal — so that order is the *nose*, and
            // the tail is its reverse. Both were the other way round to begin with, which put every lamp
            // on every car facing into the bodywork: invisible from outside, and by day indistinguishable
            // from a lamp that is simply switched off.
            if (front)
            {
                triangles.Add(start);
                triangles.Add(start + 1);
                triangles.Add(start + 2);
                triangles.Add(start);
                triangles.Add(start + 2);
                triangles.Add(start + 3);
            }
            else
            {
                triangles.Add(start);
                triangles.Add(start + 3);
                triangles.Add(start + 2);
                triangles.Add(start);
                triangles.Add(start + 2);
                triangles.Add(start + 1);
            }
        }

        private static Vector3[] BuildRing(in CarProfile profile, in Station station, int ringSubdivisions)
        {
            float z = station.Z;
            float belt = station.BeltY;
            float top = Mathf.Max(station.TopY, belt + 0.05f);

            // The arch raises the underside only, and is clamped to stay below the beltline. Letting it
            // push the belt up instead — which is what an ordering guard on the belt does — ripples the
            // hood surface right over the front wheel, because the shoulder points hang off the belt.
            float bottom = Mathf.Min(BottomAt(profile, z, station.SillY), belt - 0.08f);
            float half = station.HalfWidth;
            float topHalf = station.TopHalfWidth;
            float crown = topHalf * CrownFraction;

            // The flare is carried by the flank points only. The sill takes a third of it so the arch
            // does not look pinched underneath, the point just above the beltline takes under half so
            // the blister tucks back in towards the glasshouse, and the roof takes none at all — a
            // widebody widens the body, never the cabin.
            float flare = FlareAt(profile, z);
            float flank = half + flare;
            float sillX = half * 0.72f + flare * 0.35f;
            float shoulderX = half * 0.985f + flare * 0.45f;

            // Intermediate heights are fractions of the available span, never fixed offsets. Over a
            // wheel arch the gap between the underside and the belt shrinks to a few centimetres, and
            // fixed offsets would push these points below the underside and twist the section.
            float flankLow = Mathf.Lerp(bottom, belt, 0.30f);
            float flankHigh = Mathf.Lerp(bottom, belt, 0.70f);
            float shoulderLow = Mathf.Lerp(belt, top, 0.12f);
            float shoulderHigh = Mathf.Lerp(belt, top, 0.85f);

            // Three points across the top rather than one straight span, so roof, hood, windscreen and
            // rear window are crowned like pressed sheet metal instead of flat plates.
            var key = new[]
            {
                new Vector3(sillX, bottom, z),
                new Vector3(flank * 0.99f, flankLow, z),
                new Vector3(flank, flankHigh, z),
                new Vector3(flank, belt, z),
                new Vector3(shoulderX, shoulderLow, z),
                new Vector3(topHalf * 1.02f, shoulderHigh, z),
                new Vector3(topHalf, top, z),
                new Vector3(topHalf * 0.58f, top + crown * 0.85f, z),
                new Vector3(0f, top + crown, z),
                new Vector3(-topHalf * 0.58f, top + crown * 0.85f, z),
                new Vector3(-topHalf, top, z),
                new Vector3(-topHalf * 1.02f, shoulderHigh, z),
                new Vector3(-shoulderX, shoulderLow, z),
                new Vector3(-flank, belt, z),
                new Vector3(-flank, flankHigh, z),
                new Vector3(-flank * 0.99f, flankLow, z),
                new Vector3(-sillX, bottom, z),
            };

            var ring = new Vector3[KeyPointCount * ringSubdivisions];
            for (int segment = 0; segment < KeyPointCount; segment++)
            {
                Vector3 p0 = key[((segment - 1) + KeyPointCount) % KeyPointCount];
                Vector3 p1 = key[segment];
                Vector3 p2 = key[(segment + 1) % KeyPointCount];
                Vector3 p3 = key[(segment + 2) % KeyPointCount];

                for (int step = 0; step < ringSubdivisions; step++)
                {
                    float t = step / (float)ringSubdivisions;
                    Vector3 point = RoadPath.CatmullRom(p0, p1, p2, p3, t);
                    point.z = z;
                    ring[segment * ringSubdivisions + step] = point;
                }
            }

            return ring;
        }

        /// <summary>
        /// Glass is decided by position along the car rather than by station index, so reshaping the
        /// silhouette cannot silently move the windows onto the wrong panel.
        ///
        /// <para><b>Where the side glass stops is the single number that decides whether this reads as a
        /// coupé or as a slab.</b> It used to run back to z −1.60, past the rear axle at −1.35 and into
        /// the wheel arch flare — an unbroken dark band the whole length of the car, which is a letterbox
        /// however tall the roof above it is. Raising the greenhouse did not fix it because the fault was
        /// never the height of the band, it was the length. It now ends at −1.05, just ahead of the rear
        /// axle, and everything behind that is bodywork: the C-pillar and the sail panels that run down
        /// either side of the rear screen into the deck. That solid shoulder over the back wheel is what
        /// a fastback actually is.</para>
        ///
        /// <para>The rear screen keeps its own extent, further back than the side glass, because it lies
        /// down along the roofline between those sail panels — a top surface, not a flank.</para>
        /// </summary>
        private static int ResolveSubmesh(in CarProfile profile, float z, int keySegment)
        {
            bool windscreen = z > profile.WindscreenFrom && z < profile.WindscreenTo;
            bool rearWindow = z > profile.RearWindowFrom && z < profile.RearWindowTo;

            if (TopKeySegments.Contains(keySegment) && (windscreen || rearWindow))
            {
                return GlassSubmesh;
            }

            if (FlankKeySegments.Contains(keySegment) && InCabin(profile, z))
            {
                return GlassSubmesh;
            }

            return BodySubmesh;
        }

        /// <summary>
        /// Whether the flank carries glass at this Z — true inside any one of the profile's window
        /// bands, and false in the pillars between them.
        /// </summary>
        private static bool InCabin(in CarProfile profile, float z)
        {
            float[] cabin = profile.Cabin;

            for (int i = 0; i + 1 < cabin.Length; i += 2)
            {
                if (z > cabin[i] && z < cabin[i + 1])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The front end, in whichever of the four layouts the profile asked for.
        ///
        /// <para>Sits just <i>in front of</i> the nose cap, not at the widest station. A panel placed
        /// back where the body is widest ends up inside the shell and renders nothing.</para>
        ///
        /// <para><b>Every number here is a fraction of the profile's own face</b>, because they used to
        /// be the fastback's measurements written out. z 2.28 is two centimetres past a fastback's nose
        /// and two centimetres <i>inside</i> a van's, which seals the grille and both headlights into
        /// the bodywork — the same failure <see cref="AddTrafficLamps"/> records having had once, and
        /// one that is invisible until somebody renders the thing at night. Expressed against
        /// <see cref="CarProfile.NoseZ"/>, <see cref="LampHeight"/> and the local half-width, the front
        /// end follows whatever shape it is put on. For the fastback
        /// <see cref="HeadLampStyle.GrilleBar"/> reproduces the old literals exactly: z 2.28, grille
        /// ±0.62 spanning y −0.22…0.06, lamps 0.34…0.56 spanning −0.18…0.02.</para>
        /// </summary>
        private static void AddFrontDetails(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            float z = profile.NoseZ + 0.01f;
            float face = HalfWidthAt(profile, profile.NoseZ);

            // The lamp band's centre. A hand below the beltline is where the lens goes on a reduced body
            // (see LampHeight); the detailed face carries its lamps a further 10 cm down, tucked into the
            // top of the grille rather than sitting on the beltline.
            float lamp = LampHeight(profile, profile.NoseZ) - 0.10f;

            List<int> grille = submeshTriangles[GlassSubmesh];
            List<int> lamps = submeshTriangles[HeadlightSubmesh];
            float span = profile.GrilleSpan;

            switch (profile.HeadLamps)
            {
                case HeadLampStyle.Round:
                {
                    // A narrow upright grille between two round lamps standing outboard of it — the
                    // off-roader's face, and the one arrangement that cannot be mistaken for a car's.
                    AddPanel(vertices, grille, z, -span * face, span * face, lamp - 0.17f, lamp + 0.17f, true);

                    float radius = face * 0.155f;
                    float centre = (span + 0.155f + 0.06f) * face;

                    AddDiscPanel(vertices, lamps, z + 0.02f, centre, lamp + 0.02f, radius, 10, true);
                    AddDiscPanel(vertices, lamps, z + 0.02f, -centre, lamp + 0.02f, radius, 10, true);
                    break;
                }

                case HeadLampStyle.Slim:
                {
                    // A low wide mouth under a pair of slim lenses, which is every fast car of the
                    // nineties. The lamps are above the opening rather than set into it — that gap is
                    // what stops the face reading as one dark bar the width of the car.
                    AddPanel(vertices, grille, z, -span * face, span * face, lamp - 0.20f, lamp - 0.03f, true);

                    AddPanel(vertices, lamps, z + 0.02f, 0.20f * face, span * face, lamp + 0.05f, lamp + 0.13f, true);
                    AddPanel(vertices, lamps, z + 0.02f, -span * face, -0.20f * face, lamp + 0.05f, lamp + 0.13f, true);
                    break;
                }

                case HeadLampStyle.Stacked:
                {
                    // An upright grille with a square lamp bolted either side of it. An eighties saloon
                    // wears its face high and narrow, where a muscle car wears one wide and low.
                    AddPanel(vertices, grille, z, -span * face, span * face, lamp - 0.11f, lamp + 0.13f, true);

                    AddPanel(vertices, lamps, z + 0.02f,
                        (span + 0.04f) * face, 0.88f * face, lamp - 0.09f, lamp + 0.11f, true);
                    AddPanel(vertices, lamps, z + 0.02f,
                        -0.88f * face, -(span + 0.04f) * face, lamp - 0.09f, lamp + 0.11f, true);
                    break;
                }

                default:
                {
                    // A full-width opening with the lamps set into its outer ends, two centimetres proud
                    // of it — which is the front of a Mustang in one sentence, and is why the grille is
                    // drawn first and wide rather than as a slot between the lights.
                    const float lampInner = 0.378f;
                    const float lampOuter = 0.622f;

                    AddPanel(vertices, grille, z, -span * face, span * face, lamp - 0.14f, lamp + 0.14f, true);

                    AddPanel(vertices, lamps, z + 0.02f,
                        lampInner * face, lampOuter * face, lamp - 0.10f, lamp + 0.10f, true);
                    AddPanel(vertices, lamps, z + 0.02f,
                        -lampOuter * face, -lampInner * face, lamp - 0.10f, lamp + 0.10f, true);
                    break;
                }
            }
        }

        /// <summary>
        /// The tail lamps, in whichever of the five layouts the profile asked for.
        ///
        /// <para>Seated off <see cref="CarProfile.TailZ"/> and the tail's own width for the reason given
        /// on <see cref="AddFrontDetails"/>. For the fastback <see cref="TailLampStyle.Bars"/> at the
        /// default count and insets reproduces what every body used to wear: z −2.50, three bars a side
        /// at 0.15, 0.32 and 0.49, 0.14 wide, spanning y −0.19…0.09.</para>
        ///
        /// <para><b>This is the detail worth the most per triangle on the whole car.</b> The silhouette
        /// is only legible side-on, and side-on is the one view a player driving behind a car never has;
        /// what they see for minutes at a time is a dark panel with two bright marks on it, and the
        /// shape of those marks is the entire identity of the car in that view.</para>
        /// </summary>
        private static void AddRearDetails(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            float z = profile.TailZ - 0.01f;
            float face = HalfWidthAt(profile, profile.TailZ);
            float lamp = LampHeight(profile, profile.TailZ) - profile.TailLampDrop;
            float half = profile.TailLampHalfHeight;

            List<int> lamps = submeshTriangles[TaillightSubmesh];

            float inner = profile.TailLampInner * face;
            float outer = profile.TailLampOuter * face;
            int count = Mathf.Max(1, profile.TailLampCount);

            switch (profile.TailLamps)
            {
                case TailLampStyle.Round:
                {
                    // Round lenses in a row, the way a nineties Japanese coupé wears them. Sized from the
                    // gap they have to share rather than given a radius, so a pair and a quartet both
                    // fill the cluster instead of one of them rattling around in it.
                    float pitch = (outer - inner) / count;
                    float radius = Mathf.Min(pitch * 0.42f, half);

                    for (int i = 0; i < count; i++)
                    {
                        float centre = inner + pitch * (i + 0.5f);
                        AddDiscPanel(vertices, lamps, z, centre, lamp, radius, 10, false);
                        AddDiscPanel(vertices, lamps, z, -centre, lamp, radius, 10, false);
                    }

                    break;
                }

                case TailLampStyle.Stack:
                {
                    // One tall lamp standing in the corner of the tailgate. An estate and a van both do
                    // this and for the same reason a real one does: the glass wants the middle of the
                    // panel, so the lamps go up the sides of it.
                    AddPanel(vertices, lamps, z, inner, outer, lamp - half, lamp + half, false);
                    AddPanel(vertices, lamps, z, -outer, -inner, lamp - half, lamp + half, false);
                    break;
                }

                case TailLampStyle.Strip:
                {
                    // One band straight across, through the centre line — the tail of a car that wants
                    // to look wide.
                    AddPanel(vertices, lamps, z, -outer, outer, lamp - half, lamp + half, false);
                    break;
                }

                case TailLampStyle.Blocks:
                {
                    // Wide horizontal blocks, stacked if there is more than one. An eighties three-box
                    // carries a single deep unit; two stacked reads as the ribbed lens of one.
                    float pitch = half * 2f / count;

                    for (int i = 0; i < count; i++)
                    {
                        float centre = lamp + half - pitch * (i + 0.5f);
                        float blockHalf = pitch * 0.40f;

                        AddPanel(vertices, lamps, z, inner, outer, centre - blockHalf, centre + blockHalf, false);
                        AddPanel(vertices, lamps, z, -outer, -inner, centre - blockHalf, centre + blockHalf, false);
                    }

                    break;
                }

                default:
                {
                    // Vertical bars, which is the tail the fastback is quoting.
                    float pitch = (outer - inner) / count;
                    // 0.824 of the pitch, which with the default inset and count puts the fastback's
                    // three bars back exactly where they were as literals: 0.1786, 0.3810 and 0.5833 of
                    // the face, 0.1667 wide.
                    float barWidth = pitch * 0.824f;

                    for (int i = 0; i < count; i++)
                    {
                        float x0 = inner + pitch * i;
                        float x1 = x0 + barWidth;

                        AddPanel(vertices, lamps, z, x0, x1, lamp - half, lamp + half, false);
                        AddPanel(vertices, lamps, z, -x1, -x0, lamp - half, lamp + half, false);
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// A window in the tail panel, for the cars whose tailgate stands up.
        ///
        /// <para>See <see cref="CarProfile.TailGlassHalfWidth"/> for why this cannot come out of the
        /// station table: the loft puts glass on the top surface, and a vertical tailgate has almost no
        /// top surface to put it on. Drawn a few millimetres proud of the cap the same way the grille is
        /// drawn proud of the nose, and skipped entirely when the profile did not ask for one — so a
        /// fastback, whose backlight genuinely does lie along the roofline, is untouched.</para>
        /// </summary>
        private static void AddRearWindow(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            if (profile.TailGlassHalfWidth <= 0.0001f)
            {
                return;
            }

            float z = profile.TailZ - 0.005f;
            float half = profile.TailGlassHalfWidth * HalfWidthAt(profile, profile.TailZ);

            AddPanel(vertices, submeshTriangles[GlassSubmesh], z,
                -half, half, profile.TailGlassBottom, profile.TailGlassTop, false);
        }

        /// <summary>
        /// A spare wheel bolted to the tailgate: a capped drum standing off the tail panel with a rim
        /// face proud of it.
        ///
        /// <para>One of exactly two things on this vehicle that a driver behind it reads before its
        /// silhouette, and it costs about forty triangles.</para>
        ///
        /// <para><b>It does stand behind <see cref="CarProfile.TailZ"/>, by ten centimetres, and that is
        /// ten centimetres with no collision on it</b> — <see cref="HullBounds"/> measures the collider
        /// from the station table, as the note on <see cref="AddWing"/> explains. A carrier that clipped
        /// a guard rail before the bumper did would be worse than one that does not, and a spare wheel
        /// flush with the tailgate is a painted circle.</para>
        /// </summary>
        private static void AddSpareWheel(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            if (profile.SpareWheelRadius <= 0.0001f)
            {
                return;
            }

            float radius = profile.SpareWheelRadius;
            const float depth = 0.10f;

            // Hung under the window, and its height taken *from* the window rather than guessed
            // alongside it. The two share one flat panel and both want to be as large as they can be, so
            // a spare sized independently is a spare that grows through the glass the first time
            // somebody enlarges it — which is exactly what happened.
            float top = profile.TailGlassHalfWidth > 0.0001f
                ? profile.TailGlassBottom - 0.04f
                : LampHeight(profile, profile.TailZ) + radius * 1.25f;

            float y = top - radius;
            float outer = profile.TailZ - depth;

            // The drum runs from the outer face forward into the tailgate. AddTube caps the mouth, so
            // the face the driver behind sees is closed without a disc of its own.
            AddTube(vertices, submeshTriangles[BodySubmesh],
                new Vector3(0f, y, outer), radius, depth + 0.02f, 14);

            // The rim, a couple of millimetres proud of that face. Without it the spare is a drum, and a
            // drum on the back of a car is a barrel.
            AddDiscPanel(vertices, submeshTriangles[ChromeSubmesh],
                outer - 0.004f, 0f, y, radius * 0.52f, 12, false);
        }

        /// <summary>
        /// Two indicator turrets standing on the front wing tops. Off-roader furniture, and the only
        /// detail on any of these cars placed where its own driver can see it.
        /// </summary>
        private static void AddIndicatorTurrets(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            if (!profile.IndicatorTurrets)
            {
                return;
            }

            // On the bonnet's shoulders, a little behind the nose cap: far enough back that the last few
            // stations have not started pulling the width in under them.
            float z = profile.NoseZ - 0.30f;
            float x = HalfWidthAt(profile, z) * 0.82f;
            float y = TopYAt(profile, z) + 0.05f;

            for (int side = -1; side <= 1; side += 2)
            {
                AddBox(vertices, submeshTriangles[BodySubmesh],
                    new Vector3(side * x, y - 0.02f, z), new Vector3(0.09f, 0.06f, 0.16f));
                AddBox(vertices, submeshTriangles[HeadlightSubmesh],
                    new Vector3(side * x, y + 0.02f, z), new Vector3(0.08f, 0.05f, 0.15f));
            }
        }

        /// <summary>
        /// A round lamp lens: a fan in a constant-Z plane, wound to face the end of the car it is on.
        ///
        /// <para><see cref="AddPanel"/> cannot do it and the difference matters — four round lenses is
        /// the whole tail of one of these cars, and four rounded-off rectangles is a different car.</para>
        /// </summary>
        private static void AddDiscPanel(
            List<Vector3> vertices,
            List<int> triangles,
            float z,
            float centreX,
            float centreY,
            float radius,
            int sides,
            bool facingForward)
        {
            var centre = new Vector3(centreX, centreY, z);

            // Same trick AddPanel uses: a reference point behind the disc, so AddTriangleOutward settles
            // the winding rather than this having to reason about it.
            var inward = new Vector3(centreX, centreY, facingForward ? z - 1f : z + 1f);

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                var p0 = new Vector3(centreX + Mathf.Cos(a0) * radius, centreY + Mathf.Sin(a0) * radius, z);
                var p1 = new Vector3(centreX + Mathf.Cos(a1) * radius, centreY + Mathf.Sin(a1) * radius, z);

                AddTriangleOutward(vertices, triangles, centre, p0, p1, inward);
            }
        }

        /// <summary>
        /// The load bed: a trough dropped in through the hole <see cref="BuildShell"/> leaves in the top
        /// surface over <see cref="CarProfile.BedFrom"/>..<see cref="CarProfile.BedTo"/>.
        ///
        /// <para>Five surfaces run the length of it — a rail cap either side, an inner wall either side
        /// and the floor — plus a wall at each end, which are the bulkhead behind the cab and the inside
        /// of the tailgate. Lofted on its own grid rather than off the station list, because it wants a
        /// coarser one than the shell on a traffic body and a finer one than the key stations on the
        /// player's.</para>
        ///
        /// <para><b>The floor is not flat and that is the point.</b> It rides up over each wheel arch by
        /// the same <see cref="BottomAt"/> the underside is cut with, three centimetres clear of it — so
        /// the humps in the bed are the far side of the arches rather than a second guess at where the
        /// wheels are, and the two can never drift apart. A pickup with a flat bed floor over its rear
        /// axle is a pickup with its wheels somewhere else.</para>
        /// </summary>
        private static void AddBed(
            in CarProfile profile,
            List<Vector3> vertices,
            List<int>[] submeshTriangles,
            int ringSubdivisions)
        {
            float length = profile.BedTo - profile.BedFrom;
            if (length < 0.05f)
            {
                return;
            }

            List<int> body = submeshTriangles[BodySubmesh];

            // A traffic pickup is a silhouette at thirty metres, so its bed gets a third of the rings.
            float step = StationStep * (ringSubdivisions > 1 ? 1f : 3f);
            int rings = Mathf.Max(2, Mathf.CeilToInt(length / step) + 1);

            var section = new Vector3[rings][];
            for (int i = 0; i < rings; i++)
            {
                float z = Mathf.Lerp(profile.BedFrom, profile.BedTo, i / (float)(rings - 1));
                section[i] = BedSection(profile, z);
            }

            // Which way each of the five spans faces: the caps and the floor look up, the walls look in
            // at each other. AddTriangleOutward settles the winding from a point on the far side.
            var facing = new[] { Vector3.up, Vector3.left, Vector3.up, Vector3.right, Vector3.up };

            for (int i = 0; i < rings - 1; i++)
            {
                Vector3[] back = section[i];
                Vector3[] front = section[i + 1];

                for (int seg = 0; seg < facing.Length; seg++)
                {
                    Vector3 a = back[seg];
                    Vector3 b = back[seg + 1];
                    Vector3 c = front[seg];
                    Vector3 d = front[seg + 1];

                    Vector3 inward = (a + b + c + d) * 0.25f - facing[seg];

                    AddTriangleOutward(vertices, body, a, b, c, inward);
                    AddTriangleOutward(vertices, body, b, d, c, inward);
                }
            }

            // The bulkhead behind the cab and the inside of the tailgate. Both span the inner width, from
            // the floor at that end up to the rail.
            AddBedWall(vertices, body, section[rings - 1], Vector3.back);
            AddBedWall(vertices, body, section[0], Vector3.forward);
        }

        /// <summary>
        /// One cross-section of the trough, as six points running from the right-hand rail across to the
        /// left: rail edge, inner wall top, floor, floor, inner wall top, rail edge.
        /// </summary>
        private static Vector3[] BedSection(in CarProfile profile, float z)
        {
            float rail = TopHalfWidthAt(profile, z);
            float top = TopYAt(profile, z);
            float inner = Mathf.Max(0.1f, rail - profile.BedWallThickness);

            // The arch, three centimetres under the floor. BottomAt is handed the bed floor as its own
            // datum, so away from the axles it answers with exactly that and over one it answers with
            // the arch — the same curve the underside is cut on.
            float floor = Mathf.Min(top - 0.06f, BottomAt(profile, z, profile.BedFloorY) + 0.03f);

            return new[]
            {
                new Vector3(rail, top, z),
                new Vector3(inner, top, z),
                new Vector3(inner, floor, z),
                new Vector3(-inner, floor, z),
                new Vector3(-inner, top, z),
                new Vector3(-rail, top, z),
            };
        }

        /// <summary>One end of the bed, closing the trough between the two inner walls.</summary>
        private static void AddBedWall(
            List<Vector3> vertices, List<int> triangles, Vector3[] section, Vector3 facing)
        {
            Vector3 a = section[1];
            Vector3 b = section[2];
            Vector3 c = section[3];
            Vector3 d = section[4];

            Vector3 inward = (a + b + c + d) * 0.25f - facing;

            AddTriangleOutward(vertices, triangles, a, b, c, inward);
            AddTriangleOutward(vertices, triangles, a, c, d, inward);
        }

        /// <summary>
        /// Half the top surface's width at a given Z. The third of the trio with <see cref="TopYAt"/>
        /// and <see cref="HalfWidthAt"/>, and it exists for the bed rails.
        /// </summary>
        private static float TopHalfWidthAt(in CarProfile profile, float z)
        {
            Station[] stations = profile.Stations;

            if (z <= stations[0].Z)
            {
                return stations[0].TopHalfWidth;
            }

            for (int i = 1; i < stations.Length; i++)
            {
                if (z > stations[i].Z)
                {
                    continue;
                }

                float span = stations[i].Z - stations[i - 1].Z;
                float t = span > 0.0001f ? (z - stations[i - 1].Z) / span : 0f;

                return Mathf.Lerp(stations[i - 1].TopHalfWidth, stations[i].TopHalfWidth, t);
            }

            return stations[stations.Length - 1].TopHalfWidth;
        }

        /// <summary>
        /// A rear wing: one blade on two stalks, in the body submesh so it wears the car's paint.
        ///
        /// <para>Does nothing unless the profile asked for one, so the bodies written before this
        /// existed produce byte-identical meshes.</para>
        ///
        /// <para><b>It stands ahead of the tail, not behind it.</b> <see cref="HullBounds"/> measures the
        /// collider from the station table rather than from <c>mesh.bounds</c>, which is right — the
        /// table is the car and the details are decoration. The consequence is that anything hung past
        /// <see cref="CarProfile.TailZ"/> would have no collision at all and would pass through guard
        /// rails, so the blade is seated over the deck where the hull already is.</para>
        ///
        /// <para>Boxes rather than an aerofoil section. At the size this is drawn a cambered blade is
        /// four more rings for a silhouette nobody can tell from a plank, and the whole point of the
        /// thing is the shape it cuts against the sky.</para>
        /// </summary>
        private static void AddWing(
            in CarProfile profile, List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            if (profile.WingHalfSpan <= 0.0001f || profile.WingHeight <= 0.0001f)
            {
                return;
            }

            List<int> triangles = submeshTriangles[BodySubmesh];

            float deck = TopYAt(profile, profile.WingZ);
            float blade = deck + profile.WingHeight;
            float span = profile.WingHalfSpan;

            // The stalks stand inboard of the blade's tips, the way a wing on end plates does — flush
            // uprights read as a shelf welded to the boot rather than as something bolted on.
            float stalkX = span * 0.72f;
            const float stalkHalfWidth = 0.035f;
            const float stalkHalfLength = 0.055f;

            for (int side = -1; side <= 1; side += 2)
            {
                AddBox(
                    vertices, triangles,
                    new Vector3(side * stalkX, (deck + blade) * 0.5f, profile.WingZ),
                    new Vector3(stalkHalfWidth * 2f, blade - deck, stalkHalfLength * 2f));
            }

            // Thin and deep: 4 cm thick over 30 cm of chord.
            AddBox(
                vertices, triangles,
                new Vector3(0f, blade + 0.02f, profile.WingZ),
                new Vector3(span * 2f, 0.04f, 0.30f));

            // End plates. Two centimetres of card at each tip, and they are most of why the shape reads
            // as a wing from behind rather than as a bar.
            //
            // Centred on the blade rather than above it. Seated at blade + 0.04 they stood clear of the
            // aerofoil like a pair of fins, and in a side view — which is what the garage thumbnail is —
            // the fin was the only part of the wing you could see, so the car appeared to have a
            // periscope. A plate straddling its own blade reads as an end plate from every angle.
            for (int side = -1; side <= 1; side += 2)
            {
                AddBox(
                    vertices, triangles,
                    new Vector3(side * (span + 0.01f), blade + 0.02f, profile.WingZ),
                    new Vector3(0.02f, 0.12f, 0.34f));
            }
        }

        /// <summary>
        /// The top surface at a given Z, interpolated between key stations. The vertical twin of
        /// <see cref="HalfWidthAt"/>, and it exists for the same reason: something bolted to the
        /// bodywork has to know where the bodywork is.
        ///
        /// <para>Note this reads the table, not the built shell, so it is short of the mesh by
        /// <see cref="CrownFraction"/> — the ring bulges its top surface above its own edges. Two
        /// centimetres on a wing stalk, which is why the blade is seated a touch proud of it.</para>
        /// </summary>
        private static float TopYAt(in CarProfile profile, float z)
        {
            Station[] stations = profile.Stations;

            if (z <= stations[0].Z)
            {
                return stations[0].TopY;
            }

            for (int i = 1; i < stations.Length; i++)
            {
                if (z > stations[i].Z)
                {
                    continue;
                }

                float span = stations[i].Z - stations[i - 1].Z;
                float t = span > 0.0001f ? (z - stations[i - 1].Z) / span : 0f;

                return Mathf.Lerp(stations[i - 1].TopY, stations[i].TopY, t);
            }

            return stations[stations.Length - 1].TopY;
        }

        /// <summary>
        /// Builds a wheel with its axle along **X**, because the controller writes the wheel pivot's
        /// rotation directly as spin-about-X plus steer-about-Y.
        /// </summary>
        /// <param name="rimFraction">
        /// Rim diameter as a fraction of <paramref name="radius"/>; the remainder is sidewall. See
        /// <see cref="CarProfile.RimFraction"/> — 0.58 is the fastback's and was the literal this
        /// replaced.
        /// </param>
        public static Mesh BuildWheel(
            float radius,
            float width,
            int sides = 18,
            string meshName = "WheelMesh",
            float rimFraction = 0.58f,
            RimStyle style = RimStyle.FiveSpoke)
        {
            float halfWidth = width * 0.5f;
            float rimRadius = radius * Mathf.Clamp(rimFraction, 0.30f, 0.86f);

            var vertices = new List<Vector3>(sides * 16);
            var tyreTriangles = new List<int>(sides * 18);
            var rimTriangles = new List<int>(sides * 9);

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));

                Vector3 treadRight0 = new Vector3(halfWidth, d0.y * radius, d0.x * radius);
                Vector3 treadRight1 = new Vector3(halfWidth, d1.y * radius, d1.x * radius);
                Vector3 treadLeft0 = new Vector3(-halfWidth, d0.y * radius, d0.x * radius);
                Vector3 treadLeft1 = new Vector3(-halfWidth, d1.y * radius, d1.x * radius);

                AddTriangleOutward(vertices, tyreTriangles, treadLeft0, treadRight0, treadRight1, Vector3.zero);
                AddTriangleOutward(vertices, tyreTriangles, treadLeft0, treadRight1, treadLeft1, Vector3.zero);

                for (int side = 0; side < 2; side++)
                {
                    float x = side == 0 ? halfWidth : -halfWidth;
                    float sign = side == 0 ? -1f : 1f;
                    Vector3 inward = new Vector3(sign, 0f, 0f);

                    // Tyre sidewall: the annulus from the tread in to the rim lip.
                    Vector3 outer0 = new Vector3(x, d0.y * radius, d0.x * radius);
                    Vector3 outer1 = new Vector3(x, d1.y * radius, d1.x * radius);
                    Vector3 lip0 = new Vector3(x, d0.y * rimRadius, d0.x * rimRadius);
                    Vector3 lip1 = new Vector3(x, d1.y * rimRadius, d1.x * rimRadius);

                    AddTriangleOutward(vertices, tyreTriangles, outer0, lip0, outer1, inward);
                    AddTriangleOutward(vertices, tyreTriangles, lip0, lip1, outer1, inward);

                    // Rim lip: a solid metal ring just inside the tyre.
                    float rimX = x + sign * 0.016f;
                    float lipInner = rimRadius * 0.80f;

                    Vector3 rimOuter0 = new Vector3(rimX, d0.y * rimRadius, d0.x * rimRadius);
                    Vector3 rimOuter1 = new Vector3(rimX, d1.y * rimRadius, d1.x * rimRadius);
                    Vector3 rimInner0 = new Vector3(rimX, d0.y * lipInner, d0.x * lipInner);
                    Vector3 rimInner1 = new Vector3(rimX, d1.y * lipInner, d1.x * lipInner);

                    AddTriangleOutward(vertices, rimTriangles, rimOuter0, rimInner0, rimOuter1, inward);
                    AddTriangleOutward(vertices, rimTriangles, rimInner0, rimInner1, rimOuter1, inward);

                    // Brake disc, set deeper and dark, so the gaps between the spokes read as openings
                    // rather than as holes through the car.
                    float brakeX = x + sign * 0.055f;
                    Vector3 brakeCenter = new Vector3(brakeX, 0f, 0f);
                    Vector3 brake0 = new Vector3(brakeX, d0.y * lipInner, d0.x * lipInner);
                    Vector3 brake1 = new Vector3(brakeX, d1.y * lipInner, d1.x * lipInner);

                    AddTriangleOutward(vertices, tyreTriangles, brakeCenter, brake0, brake1, inward);
                }
            }

            AddRimFace(style, vertices, rimTriangles, halfWidth, rimRadius * 0.80f, radius * 0.16f);

            var mesh = new Mesh { name = meshName };
            mesh.indexFormat = IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.subMeshCount = WheelSubmeshCount;
            mesh.SetTriangles(tyreTriangles, TyreSubmesh);
            mesh.SetTriangles(rimTriangles, RimSubmesh);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// The face of the wheel, on both sides of it, in whichever of the five styles was asked for.
        ///
        /// <para>All five are drawn between <paramref name="lipInner"/> and a hub, and all five leave
        /// the dark brake disc showing through their gaps — that is what makes the openings read as
        /// openings onto a brake rather than as holes straight through the car, and it is why none of
        /// these needs a backing plate of its own.</para>
        /// </summary>
        private static void AddRimFace(
            RimStyle style,
            List<Vector3> vertices,
            List<int> triangles,
            float halfWidth,
            float lipInner,
            float hubRadius)
        {
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                float x = (side == 0 ? halfWidth : -halfWidth) + sign * 0.022f;
                Vector3 inward = new Vector3(sign, 0f, 0f);

                switch (style)
                {
                    case RimStyle.MultiSpoke:
                        AddRimSpokes(vertices, triangles, x, inward, lipInner, hubRadius, 10, 0.13f, 0.75f);
                        break;

                    case RimStyle.Turbine:
                        // Many shallow slats, each offset around the rim from its own root, so the face
                        // reads as turned rather than as spokes. The taper runs the other way from a
                        // spoke's: wide at the hub, narrow at the lip.
                        AddRimSlats(vertices, triangles, x, inward, lipInner, hubRadius, 12, 0.18f);
                        break;

                    case RimStyle.Steel:
                        // A solid dish with a ring of round holes punched through it. Drawn as the dish
                        // plus the holes' surrounds rather than as spokes, because that is what the
                        // shape is — and a steel wheel is the one wheel here whose character is how
                        // little of it is open.
                        AddRimDish(vertices, triangles, x, inward, lipInner, hubRadius, 5, 0.30f);
                        break;

                    case RimStyle.OffRoad:
                        AddRimSpokes(vertices, triangles, x, inward, lipInner, hubRadius, 6, 0.34f, 0.80f);
                        break;

                    default:
                        AddRimSpokes(vertices, triangles, x, inward, lipInner, hubRadius, 5, 0.30f, 0.55f);
                        break;
                }

                AddRimHub(vertices, triangles, x, inward, hubRadius, style == RimStyle.OffRoad ? 0.03f : 0f);
            }
        }

        /// <summary>
        /// <paramref name="count"/> spokes from the lip in to the hub, tapering by
        /// <paramref name="hubTaper"/> — which is what stops them looking like pie slices.
        /// </summary>
        private static void AddRimSpokes(
            List<Vector3> vertices,
            List<int> triangles,
            float x,
            Vector3 inward,
            float lipInner,
            float hubRadius,
            int count,
            float halfAngle,
            float hubTaper)
        {
            for (int i = 0; i < count; i++)
            {
                float centreAngle = i / (float)count * Mathf.PI * 2f;
                float a0 = centreAngle - halfAngle;
                float a1 = centreAngle + halfAngle;

                float hubSpread = halfAngle * hubTaper;
                float h0 = centreAngle - hubSpread;
                float h1 = centreAngle + hubSpread;

                Vector3 outerA = new Vector3(x, Mathf.Sin(a0) * lipInner, Mathf.Cos(a0) * lipInner);
                Vector3 outerB = new Vector3(x, Mathf.Sin(a1) * lipInner, Mathf.Cos(a1) * lipInner);
                Vector3 innerA = new Vector3(x, Mathf.Sin(h0) * hubRadius, Mathf.Cos(h0) * hubRadius);
                Vector3 innerB = new Vector3(x, Mathf.Sin(h1) * hubRadius, Mathf.Cos(h1) * hubRadius);

                AddTriangleOutward(vertices, triangles, innerA, outerA, outerB, inward);
                AddTriangleOutward(vertices, triangles, innerA, outerB, innerB, inward);
            }
        }

        /// <summary>
        /// Slats: like spokes, but each one's outer end is swept around the rim from its root, so the
        /// face reads as turned metal. <paramref name="sweep"/> is that offset in radians.
        /// </summary>
        private static void AddRimSlats(
            List<Vector3> vertices,
            List<int> triangles,
            float x,
            Vector3 inward,
            float lipInner,
            float hubRadius,
            int count,
            float sweep)
        {
            float halfAngle = Mathf.PI / count * 0.62f;

            for (int i = 0; i < count; i++)
            {
                float root = i / (float)count * Mathf.PI * 2f;
                float tip = root + sweep;

                Vector3 outerA = new Vector3(
                    x, Mathf.Sin(tip - halfAngle * 0.45f) * lipInner, Mathf.Cos(tip - halfAngle * 0.45f) * lipInner);
                Vector3 outerB = new Vector3(
                    x, Mathf.Sin(tip + halfAngle * 0.45f) * lipInner, Mathf.Cos(tip + halfAngle * 0.45f) * lipInner);
                Vector3 innerA = new Vector3(
                    x, Mathf.Sin(root - halfAngle) * hubRadius, Mathf.Cos(root - halfAngle) * hubRadius);
                Vector3 innerB = new Vector3(
                    x, Mathf.Sin(root + halfAngle) * hubRadius, Mathf.Cos(root + halfAngle) * hubRadius);

                AddTriangleOutward(vertices, triangles, innerA, outerA, outerB, inward);
                AddTriangleOutward(vertices, triangles, innerA, outerB, innerB, inward);
            }
        }

        /// <summary>
        /// A solid dish with <paramref name="holeCount"/> round holes left in it. The dish is drawn as a
        /// fan of quads between the hub and the lip, with the wedges that a hole falls in split around
        /// it — cheaper than a boolean, and at this size indistinguishable from one.
        /// </summary>
        private static void AddRimDish(
            List<Vector3> vertices,
            List<int> triangles,
            float x,
            Vector3 inward,
            float lipInner,
            float hubRadius,
            int holeCount,
            float holeHalfAngle)
        {
            const int segments = 20;
            float holeInner = Mathf.Lerp(hubRadius, lipInner, 0.28f);
            float holeOuter = Mathf.Lerp(hubRadius, lipInner, 0.80f);

            for (int i = 0; i < segments; i++)
            {
                float a0 = i / (float)segments * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)segments * Mathf.PI * 2f;

                bool overHole = false;
                for (int hole = 0; hole < holeCount; hole++)
                {
                    float centre = hole / (float)holeCount * Mathf.PI * 2f;
                    float delta = Mathf.Abs(Mathf.DeltaAngle(
                        (a0 + a1) * 0.5f * Mathf.Rad2Deg, centre * Mathf.Rad2Deg)) * Mathf.Deg2Rad;

                    if (delta < holeHalfAngle)
                    {
                        overHole = true;
                        break;
                    }
                }

                // Over a hole the dish is drawn as two rings — inboard of it and outboard of it — and
                // the gap between them is the hole. Everywhere else it is one solid wedge.
                if (overHole)
                {
                    AddRimWedge(vertices, triangles, x, inward, a0, a1, hubRadius, holeInner);
                    AddRimWedge(vertices, triangles, x, inward, a0, a1, holeOuter, lipInner);
                }
                else
                {
                    AddRimWedge(vertices, triangles, x, inward, a0, a1, hubRadius, lipInner);
                }
            }
        }

        /// <summary>One quad of a dish, between two angles and two radii.</summary>
        private static void AddRimWedge(
            List<Vector3> vertices,
            List<int> triangles,
            float x,
            Vector3 inward,
            float a0,
            float a1,
            float innerRadius,
            float outerRadius)
        {
            Vector3 innerA = new Vector3(x, Mathf.Sin(a0) * innerRadius, Mathf.Cos(a0) * innerRadius);
            Vector3 innerB = new Vector3(x, Mathf.Sin(a1) * innerRadius, Mathf.Cos(a1) * innerRadius);
            Vector3 outerA = new Vector3(x, Mathf.Sin(a0) * outerRadius, Mathf.Cos(a0) * outerRadius);
            Vector3 outerB = new Vector3(x, Mathf.Sin(a1) * outerRadius, Mathf.Cos(a1) * outerRadius);

            AddTriangleOutward(vertices, triangles, innerA, outerA, outerB, inward);
            AddTriangleOutward(vertices, triangles, innerA, outerB, innerB, inward);
        }

        /// <summary>
        /// The hub cap. <paramref name="stand"/> raises it off the face, which on an off-roader is a
        /// locking hub and is most of what says the wheel is driven rather than carried.
        /// </summary>
        private static void AddRimHub(
            List<Vector3> vertices,
            List<int> triangles,
            float x,
            Vector3 inward,
            float hubRadius,
            float stand)
        {
            const int hubSides = 10;
            float capX = x + inward.x * stand;
            Vector3 centre = new Vector3(capX, 0f, 0f);

            for (int i = 0; i < hubSides; i++)
            {
                float a0 = i / (float)hubSides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)hubSides * Mathf.PI * 2f;

                Vector3 p0 = new Vector3(capX, Mathf.Sin(a0) * hubRadius, Mathf.Cos(a0) * hubRadius);
                Vector3 p1 = new Vector3(capX, Mathf.Sin(a1) * hubRadius, Mathf.Cos(a1) * hubRadius);

                AddTriangleOutward(vertices, triangles, centre, p0, p1, inward);

                if (stand > 0.0001f)
                {
                    // The barrel of a proud hub, so it is a drum rather than a disc hovering off the
                    // face of the wheel.
                    Vector3 b0 = new Vector3(x, p0.y, p0.z);
                    Vector3 b1 = new Vector3(x, p1.y, p1.z);
                    AddTriangleOutward(vertices, triangles, b0, p0, p1, new Vector3(capX, 0f, 0f));
                    AddTriangleOutward(vertices, triangles, b0, p1, b1, new Vector3(capX, 0f, 0f));
                }
            }
        }

        private static void AddCap(List<Vector3> vertices, List<int> triangles, Vector3[] ring, bool facingForward)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < ring.Length; i++)
            {
                center += ring[i];
            }

            center /= ring.Length;

            int centerIndex = vertices.Count;
            vertices.Add(center);

            int ringStart = vertices.Count;
            vertices.AddRange(ring);

            for (int i = 0; i < ring.Length; i++)
            {
                int next = (i + 1) % ring.Length;

                // The ring runs counter-clockwise seen from +Z, so the nose keeps that order and the
                // tail reverses it.
                triangles.Add(centerIndex);
                triangles.Add(ringStart + (facingForward ? i : next));
                triangles.Add(ringStart + (facingForward ? next : i));
            }
        }

        private static void AddPanel(
            List<Vector3> vertices,
            List<int> triangles,
            float z,
            float minX,
            float maxX,
            float minY,
            float maxY,
            bool facingForward)
        {
            var a = new Vector3(minX, minY, z);
            var b = new Vector3(maxX, minY, z);
            var c = new Vector3(maxX, maxY, z);
            var d = new Vector3(minX, maxY, z);

            var inward = new Vector3(0f, (minY + maxY) * 0.5f, facingForward ? z - 1f : z + 1f);

            AddTriangleOutward(vertices, triangles, a, b, c, inward);
            AddTriangleOutward(vertices, triangles, a, c, d, inward);
        }

        /// <summary>
        /// An axis-aligned box, as twelve triangles wound outwards.
        ///
        /// <para>Written because <see cref="AddPanel"/> cannot do it: that draws a quad in a constant-Z
        /// plane, and every face of a wing except its end plates lies in a constant-Y or constant-X one.
        /// Both hand off to <see cref="AddTriangleOutward"/>, so the winding comes from the same place
        /// and nothing here has to reason about it.</para>
        /// </summary>
        private static void AddBox(
            List<Vector3> vertices, List<int> triangles, Vector3 centre, Vector3 size)
        {
            Vector3 half = size * 0.5f;

            for (int axis = 0; axis < 3; axis++)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    // The two in-plane axes of this face.
                    int u = (axis + 1) % 3;
                    int v = (axis + 2) % 3;

                    var corner = new Vector3[4];
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 point = centre;
                        point[axis] += side * half[axis];
                        point[u] += ((i == 0 || i == 3) ? -1f : 1f) * half[u];
                        point[v] += (i < 2 ? -1f : 1f) * half[v];
                        corner[i] = point;
                    }

                    AddTriangleOutward(vertices, triangles, corner[0], corner[1], corner[2], centre);
                    AddTriangleOutward(vertices, triangles, corner[0], corner[2], corner[3], centre);
                }
            }
        }

        /// <summary>A short capped tube along Z, used for the tailpipes.</summary>
        private static void AddTube(
            List<Vector3> vertices,
            List<int> triangles,
            Vector3 mouth,
            float radius,
            float length,
            int sides)
        {
            Vector3 back = mouth;
            Vector3 front = mouth + new Vector3(0f, 0f, length);
            Vector3 axis = (back + front) * 0.5f;

            for (int i = 0; i < sides; i++)
            {
                float a0 = i / (float)sides * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)sides * Mathf.PI * 2f;

                Vector3 o0 = new Vector3(Mathf.Cos(a0) * radius, Mathf.Sin(a0) * radius, 0f);
                Vector3 o1 = new Vector3(Mathf.Cos(a1) * radius, Mathf.Sin(a1) * radius, 0f);

                Vector3 b0 = back + o0;
                Vector3 b1 = back + o1;
                Vector3 f0 = front + o0;
                Vector3 f1 = front + o1;

                Vector3 sideReference = new Vector3(axis.x, axis.y, (b0.z + f0.z) * 0.5f);
                AddTriangleOutward(vertices, triangles, b0, b1, f0, sideReference);
                AddTriangleOutward(vertices, triangles, b1, f1, f0, sideReference);

                // Mouth ring, so the pipe reads as hollow rather than a peg.
                AddTriangleOutward(vertices, triangles, back, b0, b1, front);
            }
        }

        private static void AddTriangleOutward(
            List<Vector3> vertices,
            List<int> triangles,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 inwardReference)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 0.0000001f)
            {
                return;
            }

            Vector3 centroid = (a + b + c) / 3f;
            if (Vector3.Dot(normal, centroid - inwardReference) < 0f)
            {
                (b, c) = (c, b);
            }

            int baseIndex = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);

            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
        }
    }
}
