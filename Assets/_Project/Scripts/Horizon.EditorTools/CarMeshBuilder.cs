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
    /// <para>There are five profiles. <see cref="Fastback"/> is the player's car — a late-sixties
    /// American fastback: long hood, short deck, low roof, a roofline running unbroken from the cabin to
    /// the tail panel, and wide haunches over the rear wheels. The other four exist so that ambient
    /// traffic is not two dozen copies of it, and all five share a wheelbase, a track and a wheel, which
    /// is what keeps the arches, the flares and the ride height one problem rather than five.</para>
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
        /// Half the distance between the wheel centres. Must match the prefab's anchors. Set so the
        /// tyre stands a few centimetres proud of the fender — flush wheels read as recessed.
        /// </summary>
        public const float TrackHalfWidth = 0.99f;

        /// <summary>How far the widebody arches blister out beyond the flank, metres.</summary>
        private const float FlareWidth = 0.09f;

        /// <summary>How far either side of a wheel centre the flare fades back to nothing, metres.</summary>
        private const float FlareReach = 0.75f;

        /// <summary>Distance of the wheel centres from the car's middle, along Z.</summary>
        public const float WheelBaseHalf = 1.35f;

        /// <summary>
        /// How far a traffic car's origin sits above the road, metres — which is the same thing as how
        /// far the body's local origin is above the ground.
        ///
        /// The player's car gets there through its suspension: the wheel hangs at
        /// <c>-SuspensionRestLength</c> and the tyre reaches a radius below that. A traffic car has no
        /// suspension, so it is told the answer. Public because <c>TrafficDirector</c> has to lift its
        /// agents by exactly this much or they drive with their sills in the tarmac.
        /// </summary>
        public const float TrafficRideHeight = 0.74f;

        /// <summary>Local position of each tailpipe mouth, for hanging the smoke emitters on.</summary>
        public static readonly Vector3[] ExhaustOutlets =
        {
            new Vector3(0.42f, -0.44f, -2.52f),
            new Vector3(-0.42f, -0.44f, -2.52f),
        };

        /// <summary>
        /// Top of the wheel arch openings. Sized so the wheel nearly fills the opening — an arch much
        /// larger than its wheel makes the car look like it is on the wrong rims.
        ///
        /// <para>Note this is a *request*, not the final height: <see cref="BuildRing"/> clamps the arch
        /// to <c>belt - 0.08</c> so the opening can never reach the beltline.</para>
        ///
        /// <para><b>0.16 is not a taste call, it is arithmetic.</b> The wheel centre hangs at
        /// <c>-SuspensionRestLength</c>, which is -0.30, so with a 0.44 m tyre the tread tops out at
        /// +0.14. At the 0.22 this sat at, every arch stood a full ten centimetres clear of its own tyre
        /// — the single reason the car looked as though it were on the wrong rims, and worth far more
        /// than any amount of extra tyre would have been. Two centimetres of clearance is what a car
        /// actually has.</para>
        /// </summary>
        private const float ArchTopY = 0.16f;

        /// <summary>
        /// Half-length of an arch opening along Z. Roughly the wheel radius plus a margin.
        ///
        /// 0.50 exactly, because <c>WheelBaseHalf - 0.50 = 0.85</c> lands on the cowl crease, where the
        /// arch contributes nothing anyway — so the front opening cannot ripple the base of the
        /// windscreen.
        /// </summary>
        private const float ArchHalfLength = 0.50f;

        /// <summary>
        /// One car's silhouette, as a value.
        ///
        /// <para><b>This exists because a shape was five things and only one of them was the station
        /// table.</b> The glass bands, the crease positions and the Z the lamps are seated at were all
        /// bare literals scattered through the builder, every one of them a fact about the fastback
        /// rather than about cars. A second body type is wrong in all of them, and wrong quietly: put a
        /// van's lamps at the fastback's nose and they end up sealed inside the bodywork, which is a
        /// failure this file has already had once and only found because somebody rendered the town at
        /// night.</para>
        ///
        /// <para>Note what is <i>not</i> here. <see cref="TrackHalfWidth"/>, <see cref="WheelBaseHalf"/>,
        /// the wheel radius and <see cref="TrafficRideHeight"/> stay global, so every body type sits on
        /// the same wheels at the same wheelbase. That keeps <see cref="FlareAt"/>, <see cref="BottomAt"/>,
        /// the wheel seating and <c>TrafficDirector.rideHeight</c> working untouched, and a wheelbase
        /// that varied per profile would buy variety no player can see from thirty metres through
        /// fog.</para>
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

            /// <summary>And the backlight. Equal values switch it off — a pickup has no rear window.</summary>
            public readonly float RearWindowFrom;

            public readonly float RearWindowTo;

            /// <summary>Z range over which the flanks carry side glass.</summary>
            public readonly float CabinFrom;

            public readonly float CabinTo;

            /// <summary>
            /// Where the reduced body's lamp panels are seated.
            ///
            /// <b>On the caps, not near them.</b> A value a comfortable margin inside the shell puts
            /// every lamp on every car inside the bodywork, and nothing about that is visible by day.
            /// </summary>
            public readonly float NoseZ;

            public readonly float TailZ;

            public CarProfile(
                string name,
                Station[] stations,
                float[] creaseZ,
                float windscreenFrom,
                float windscreenTo,
                float rearWindowFrom,
                float rearWindowTo,
                float cabinFrom,
                float cabinTo,
                float noseZ,
                float tailZ)
            {
                Name = name;
                Stations = stations;
                CreaseZ = creaseZ;
                WindscreenFrom = windscreenFrom;
                WindscreenTo = windscreenTo;
                RearWindowFrom = rearWindowFrom;
                RearWindowTo = rearWindowTo;
                CabinFrom = cabinFrom;
                CabinTo = cabinTo;
                NoseZ = noseZ;
                TailZ = tailZ;
            }
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
        /// The player's car, and one of the five shapes on the road.
        ///
        /// Every number in it is the one that was there before this became a profile — the glass bands,
        /// the creases and the lamp seating were literals inside the builder, and lifting them out is
        /// meant to change nothing about this car at all.
        /// </summary>
        public static readonly CarProfile Fastback = new CarProfile(
            "Fastback", KeyStations, CreaseZ,
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -1.55f, rearWindowTo: -0.45f,
            cabinFrom: -1.05f, cabinTo: 0.27f,
            noseZ: 2.27f, tailZ: -2.49f);

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
            new Station(-1.95f, 1.01f, 0.30f, 0.75f, 0.68f, -0.59f),
            new Station(-1.60f, 1.03f, 0.31f, 0.75f, 0.68f, -0.59f),
            new Station(-1.35f, 1.04f, 0.31f, 0.75f, 0.68f, -0.59f),
            new Station(-1.10f, 1.02f, 0.29f, 0.75f, 0.67f, -0.59f),
            new Station(-0.80f, 0.99f, 0.26f, 0.73f, 0.64f, -0.59f),

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
            new Station(0.30f, 1.04f, 0.61f, 1.21f, 0.88f, -0.59f),
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
        /// A pickup: the fastback's nose and a two-seat cab, then an open deck to the tail.
        ///
        /// The step down from the cab roof at 0.70 to the bed rail at 0.36 happens over six centimetres,
        /// which is a vertical panel and needs a crease at both of its edges or the normals smear the
        /// back of the cab into the load bed and the whole thing reads as a melted estate.
        /// </summary>
        private static readonly Station[] PickupStations =
        {
            //           z       halfW  belt   top    topHalf sill
            new Station(-2.55f, 0.92f, 0.28f, 0.33f, 0.86f, -0.48f),
            new Station(-2.48f, 0.98f, 0.30f, 0.35f, 0.90f, -0.53f),
            new Station(-2.35f, 1.02f, 0.31f, 0.36f, 0.94f, -0.57f),

            // The bed. TopHalfWidth runs close to HalfWidth along here on purpose: a load bed is flat
            // to its rails, unlike a roof, which tucks in.
            new Station(-1.90f, 1.04f, 0.32f, 0.37f, 0.96f, -0.59f),
            new Station(-1.35f, 1.05f, 0.33f, 0.38f, 0.97f, -0.59f),
            new Station(-0.90f, 1.03f, 0.32f, 0.37f, 0.95f, -0.59f),
            new Station(-0.78f, 1.01f, 0.30f, 0.36f, 0.92f, -0.59f),

            // The back of the cab.
            new Station(-0.72f, 0.99f, 0.28f, 0.64f, 0.60f, -0.59f),
            new Station(-0.45f, 0.98f, 0.26f, 0.70f, 0.60f, -0.59f),
            new Station(0.25f, 0.97f, 0.25f, 0.70f, 0.60f, -0.59f),

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
            new Station(-2.10f, 0.84f, 0.22f, 0.42f, 0.62f, -0.46f),
            new Station(-2.04f, 0.90f, 0.24f, 0.54f, 0.66f, -0.52f),
            new Station(-1.95f, 0.95f, 0.26f, 0.62f, 0.66f, -0.56f),

            new Station(-1.70f, 0.98f, 0.28f, 0.65f, 0.64f, -0.59f),
            new Station(-1.35f, 0.99f, 0.29f, 0.66f, 0.62f, -0.59f),
            new Station(-1.05f, 0.96f, 0.27f, 0.66f, 0.60f, -0.59f),
            new Station(-0.45f, 0.94f, 0.25f, 0.66f, 0.59f, -0.59f),
            new Station(0.25f, 0.94f, 0.25f, 0.66f, 0.59f, -0.59f),

            new Station(0.80f, 0.95f, 0.26f, 0.32f, 0.76f, -0.59f),
            new Station(1.10f, 0.98f, 0.27f, 0.33f, 0.78f, -0.59f),
            new Station(1.40f, 0.99f, 0.28f, 0.33f, 0.78f, -0.58f),
            new Station(1.70f, 0.97f, 0.27f, 0.32f, 0.77f, -0.57f),
            new Station(1.88f, 0.93f, 0.25f, 0.31f, 0.74f, -0.55f),
            new Station(2.00f, 0.87f, 0.21f, 0.28f, 0.68f, -0.50f),
        };

        public static readonly CarProfile Estate = new CarProfile(
            "Estate", EstateStations, new[] { -2.24f, 0.25f, 0.85f },
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -2.42f, rearWindowTo: -2.22f,
            cabinFrom: -2.15f, cabinTo: 0.27f,
            noseZ: 2.27f, tailZ: -2.41f);

        public static readonly CarProfile Van = new CarProfile(
            "Van", VanStations, new[] { -2.40f, 1.30f, 1.62f },
            windscreenFrom: 1.28f, windscreenTo: 1.64f,

            // A panel van has no back window, and an equal pair is how a profile says so.
            rearWindowFrom: 0f, rearWindowTo: 0f,
            cabinFrom: 0.55f, cabinTo: 1.32f,
            noseZ: 2.31f, tailZ: -2.56f);

        public static readonly CarProfile Pickup = new CarProfile(
            "Pickup", PickupStations, new[] { -2.35f, -0.78f, -0.72f, 0.25f, 0.85f },
            windscreenFrom: 0.25f, windscreenTo: 0.85f,
            rearWindowFrom: -0.75f, rearWindowTo: -0.69f,
            cabinFrom: -0.70f, cabinTo: 0.27f,
            noseZ: 2.27f, tailZ: -2.56f);

        public static readonly CarProfile Hatchback = new CarProfile(
            "Hatchback", HatchbackStations, new[] { -1.95f, 0.25f, 0.80f },
            windscreenFrom: 0.25f, windscreenTo: 0.82f,
            rearWindowFrom: -2.06f, rearWindowTo: -1.93f,
            cabinFrom: -1.60f, cabinTo: 0.27f,
            noseZ: 2.01f, tailZ: -2.11f);

        /// <summary>
        /// The shapes ambient traffic is built from.
        ///
        /// <para>The fastback is in here as well as being the player's car, and that is deliberate: the
        /// player's own model appearing in traffic is what makes it one car among many rather than the
        /// only one of its kind in the world.</para>
        /// </summary>
        public static readonly CarProfile[] TrafficProfiles = { Fastback, Estate, Van, Pickup, Hatchback };

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

        /// <summary>Builds the player's car body. Five submeshes — see the Submesh constants for the order.</summary>
        public static Mesh BuildBody(string meshName = "CarBodyMesh")
        {
            CarProfile profile = Fastback;
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
            return BuildShell(profile, stations, 1, false, $"TrafficCarMesh_{profile.Name}",
                mergeGlass: true, usedSubmeshes: usedSubmeshes);
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
            List<int> usedSubmeshes = null)
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

                Vector3[] ring = BuildRing(station, ringSubdivisions);
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

                for (int i = 0; i < ringVertexCount; i++)
                {
                    int next = (i + 1) % ringVertexCount;
                    int submesh = ResolveSubmesh(profile, midZ, i / ringSubdivisions);
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
            AddCap(vertices, submeshTriangles[BodySubmesh], BuildRing(stations[0], ringSubdivisions),
                facingForward: false);
            AddCap(vertices, submeshTriangles[BodySubmesh],
                BuildRing(stations[stations.Count - 1], ringSubdivisions), facingForward: true);

            if (details)
            {
                AddFrontDetails(vertices, submeshTriangles);
                AddRearDetails(vertices, submeshTriangles);

                // Long enough to run back under the tail rather than poke out of it like a peg.
                for (int i = 0; i < ExhaustOutlets.Length; i++)
                {
                    AddTube(vertices, submeshTriangles[ChromeSubmesh], ExhaustOutlets[i], 0.075f, 0.38f, 12);
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
        private static float FlareAt(float z)
        {
            float flare = 0f;

            for (int side = -1; side <= 1; side += 2)
            {
                float distance = Mathf.Abs(z - side * WheelBaseHalf) / FlareReach;
                if (distance >= 1f)
                {
                    continue;
                }

                flare = Mathf.Max(flare, FlareWidth * Mathf.SmoothStep(0f, 1f, 1f - distance));
            }

            return flare;
        }

        /// <summary>
        /// Underside height at a given Z. Rises into a roughly circular arch over each wheel — that
        /// opening is what stops the wheels looking like castors bolted under a slab.
        /// </summary>
        private static float BottomAt(float z, float sillY)
        {
            float bottom = sillY;

            for (int side = -1; side <= 1; side += 2)
            {
                float distance = Mathf.Abs(z - side * WheelBaseHalf) / ArchHalfLength;
                if (distance >= 1f)
                {
                    continue;
                }

                float arch = Mathf.Lerp(sillY, ArchTopY, Mathf.Sqrt(1f - distance * distance));
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

            AddLampPanel(vertices, submeshTriangles[HeadlightSubmesh], 0.30f, noseY, noseZ, 0.18f, 0.08f);
            AddLampPanel(vertices, submeshTriangles[HeadlightSubmesh], -0.30f, noseY, noseZ, 0.18f, 0.08f);
            AddLampPanel(vertices, submeshTriangles[TaillightSubmesh], 0.45f, tailY, tailZ, 0.22f, 0.09f);
            AddLampPanel(vertices, submeshTriangles[TaillightSubmesh], -0.45f, tailY, tailZ, 0.22f, 0.09f);

            // Seated so the tyre touches the road the director puts the car on: TrafficDirector lifts the
            // transform by its ride height, which places the ground at y = -0.55 in this frame.
            // The same wheel the player's car hangs, at the same height: both bodies come off the same
            // station table, so a traffic car that sat on different rubber would be a different car.
            const float radius = 0.44f;
            const float centreY = -TrafficRideHeight + radius;

            // At TrackHalfWidth, not inboard of it. That constant is set so a tyre stands proud of the
            // widebody flare — put the wheel a hand's width further in, as this first did, and the
            // bodywork swallows it and the car has no wheels at all.
            for (int i = 0; i < 4; i++)
            {
                float x = (i & 1) == 0 ? -TrackHalfWidth : TrackHalfWidth;
                float z = (i & 2) == 0 ? -WheelBaseHalf : WheelBaseHalf;

                AddTrafficWheel(vertices, submeshTriangles[ChromeSubmesh],
                    new Vector3(x, centreY, z), radius, 0.13f, 8);
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

            Station[] stations = profile.Stations;

            // Off either end, the nearest station is the answer: a lamp is seated on a cap, and the cap
            // is the last station.
            if (z <= stations[0].Z)
            {
                return stations[0].BeltY - belowBelt;
            }

            for (int i = 1; i < stations.Length; i++)
            {
                if (z > stations[i].Z)
                {
                    continue;
                }

                float span = stations[i].Z - stations[i - 1].Z;
                float t = span > 0.0001f ? (z - stations[i - 1].Z) / span : 0f;

                return Mathf.Lerp(stations[i - 1].BeltY, stations[i].BeltY, t) - belowBelt;
            }

            return stations[stations.Length - 1].BeltY - belowBelt;
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

        private static Vector3[] BuildRing(in Station station, int ringSubdivisions)
        {
            float z = station.Z;
            float belt = station.BeltY;
            float top = Mathf.Max(station.TopY, belt + 0.05f);

            // The arch raises the underside only, and is clamped to stay below the beltline. Letting it
            // push the belt up instead — which is what an ordering guard on the belt does — ripples the
            // hood surface right over the front wheel, because the shoulder points hang off the belt.
            float bottom = Mathf.Min(BottomAt(z, station.SillY), belt - 0.08f);
            float half = station.HalfWidth;
            float topHalf = station.TopHalfWidth;
            float crown = topHalf * CrownFraction;

            // The flare is carried by the flank points only. The sill takes a third of it so the arch
            // does not look pinched underneath, the point just above the beltline takes under half so
            // the blister tucks back in towards the glasshouse, and the roof takes none at all — a
            // widebody widens the body, never the cabin.
            float flare = FlareAt(z);
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
            bool cabin = z > profile.CabinFrom && z < profile.CabinTo;

            if (TopKeySegments.Contains(keySegment) && (windscreen || rearWindow))
            {
                return GlassSubmesh;
            }

            if (FlankKeySegments.Contains(keySegment) && cabin)
            {
                return GlassSubmesh;
            }

            return BodySubmesh;
        }

        /// <summary>
        /// Wide grille with the headlights set into its outer ends.
        ///
        /// Sits just *in front of* the nose cap at z 2.44, not at the widest station. A panel placed
        /// back where the body is widest ends up inside the shell and renders nothing.
        /// </summary>
        private static void AddFrontDetails(List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            // Two centimetres ahead of the nose cap, which now ends at 2.26 and is ±0.86 wide. A panel
            // placed back where the body is widest ends up buried inside the shell and renders nothing.
            //
            // Wider than it was, because the cap it sits on is wider: the face is upright now instead of
            // domed, so there is a real panel to put a grille on. A Mustang's grille runs nearly the
            // full width with the lamps set into its outer ends, and that full-width bar is as much of
            // the front-end read as the shape of the nose is.
            const float z = 2.28f;

            // A full-width opening with the lamps set into its outer ends, two centimetres proud of it —
            // which is the front of a Mustang in one sentence, and is why the grille is drawn first and
            // wide rather than as a slot between the lights. It spans 1.24 m of a 1.80 m face and 0.28 m
            // of its 0.78 m height; the previous 0.80 by 0.17 read as a letterbox on a blank panel.
            AddPanel(vertices, submeshTriangles[GlassSubmesh], z, -0.62f, 0.62f, -0.22f, 0.06f, true);

            AddPanel(vertices, submeshTriangles[HeadlightSubmesh], z + 0.02f, 0.34f, 0.56f, -0.18f, 0.02f, true);
            AddPanel(vertices, submeshTriangles[HeadlightSubmesh], z + 0.02f, -0.56f, -0.34f, -0.18f, 0.02f, true);
        }

        /// <summary>Three vertical bars each side, which is the tail this car is quoting.</summary>
        private static void AddRearDetails(List<Vector3> vertices, List<int>[] submeshTriangles)
        {
            // Two centimetres behind the tail cap, which now ends at -2.48.
            const float z = -2.50f;
            var barStarts = new[] { 0.15f, 0.32f, 0.49f };
            const float barWidth = 0.14f;

            for (int i = 0; i < barStarts.Length; i++)
            {
                float x0 = barStarts[i];
                float x1 = x0 + barWidth;

                AddPanel(vertices, submeshTriangles[TaillightSubmesh], z, x0, x1, -0.19f, 0.09f, false);
                AddPanel(vertices, submeshTriangles[TaillightSubmesh], z, -x1, -x0, -0.19f, 0.09f, false);
            }
        }

        /// <summary>
        /// Builds a wheel with its axle along **X**, because the controller writes the wheel pivot's
        /// rotation directly as spin-about-X plus steer-about-Y.
        /// </summary>
        public static Mesh BuildWheel(float radius, float width, int sides = 18, string meshName = "WheelMesh")
        {
            float halfWidth = width * 0.5f;
            float rimRadius = radius * 0.58f;

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

            AddSpokes(vertices, rimTriangles, halfWidth, rimRadius * 0.80f, radius * 0.16f);

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
        /// Five spokes and a centre hub on both faces of the wheel. Five is the count that reads as a
        /// muscle-car wheel; the dark brake disc sits behind, so the gaps between spokes look like
        /// openings onto the brake rather than holes straight through the wheel.
        /// </summary>
        private static void AddSpokes(
            List<Vector3> vertices,
            List<int> triangles,
            float halfWidth,
            float lipInner,
            float hubRadius)
        {
            const int spokeCount = 5;
            const float spokeHalfAngle = 0.30f;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                float x = (side == 0 ? halfWidth : -halfWidth) + sign * 0.022f;
                Vector3 inward = new Vector3(sign, 0f, 0f);
                Vector3 hubCenter = new Vector3(x, 0f, 0f);

                for (int i = 0; i < spokeCount; i++)
                {
                    float centreAngle = i / (float)spokeCount * Mathf.PI * 2f;
                    float a0 = centreAngle - spokeHalfAngle;
                    float a1 = centreAngle + spokeHalfAngle;

                    // Spokes taper towards the hub, which is what stops them looking like pie slices.
                    float hubSpread = spokeHalfAngle * 0.55f;
                    float h0 = centreAngle - hubSpread;
                    float h1 = centreAngle + hubSpread;

                    Vector3 outerA = new Vector3(x, Mathf.Sin(a0) * lipInner, Mathf.Cos(a0) * lipInner);
                    Vector3 outerB = new Vector3(x, Mathf.Sin(a1) * lipInner, Mathf.Cos(a1) * lipInner);
                    Vector3 innerA = new Vector3(x, Mathf.Sin(h0) * hubRadius, Mathf.Cos(h0) * hubRadius);
                    Vector3 innerB = new Vector3(x, Mathf.Sin(h1) * hubRadius, Mathf.Cos(h1) * hubRadius);

                    AddTriangleOutward(vertices, triangles, innerA, outerA, outerB, inward);
                    AddTriangleOutward(vertices, triangles, innerA, outerB, innerB, inward);
                }

                // Hub cap.
                const int hubSides = 10;
                for (int i = 0; i < hubSides; i++)
                {
                    float a0 = i / (float)hubSides * Mathf.PI * 2f;
                    float a1 = (i + 1) / (float)hubSides * Mathf.PI * 2f;

                    Vector3 p0 = new Vector3(x, Mathf.Sin(a0) * hubRadius, Mathf.Cos(a0) * hubRadius);
                    Vector3 p1 = new Vector3(x, Mathf.Sin(a1) * hubRadius, Mathf.Cos(a1) * hubRadius);

                    AddTriangleOutward(vertices, triangles, hubCenter, p0, p1, inward);
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
