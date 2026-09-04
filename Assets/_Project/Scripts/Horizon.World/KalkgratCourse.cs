using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// On from the Ebental: a fast climb over the Kalkgrat, a tunnel through its crest, and a steep
    /// drop down the Steilufer to the water.
    ///
    /// <para><b>Two roads in one file, and they are opposites on purpose.</b> The Ebental is the
    /// opposite of the pass — nothing under 150 m radius, nothing over 3 % — and repeating that trick
    /// once more would have produced a third variation on open country. So the climb takes the
    /// Ebental's one good idea (the fast stretch is also the one that loads the engine) and states it
    /// properly: three and a half kilometres of 240–450 m sweepers at 6 %, where the corners are quick
    /// and the work is all in the throttle. The descent then does the reverse.</para>
    ///
    /// <para><b>The descent is not a second pass, and the difference is the radius.</b> The pass lives
    /// on 20 m hairpins taken at walking pace; nothing here is under 38 m. That is fast enough to carry
    /// speed between the corners and tight enough that they have to be braked for.</para>
    ///
    /// <para><b>How steep the ground comes out is set by the legs, not by the corners.</b>
    /// <see cref="MountainField"/> derives the mountain from the road, so the slope between two stacked
    /// legs is decided by how far apart they are in plan. The first version kept the open corners and
    /// gave them 180–240 m legs, which stacked about 150 m apart and produced a broad hillside the
    /// gallery sat on top of like a lump rather than being cut into. The legs are now 90–140 m and the
    /// corners are untouched: the descent still turns at 38 to 70 m where the pass turns at 20, and it
    /// falls down something worth putting a gallery on.</para>
    ///
    /// <para>The legs are also what decides where the road comes out. At 220 m each it reached the coast
    /// at 13 m, and the corniche below ran barely thirty metres over the water with a wall of trees in
    /// between — see <see cref="MeerengeCourse"/> for what that cost. At 130 the descent gives back a
    /// hundred metres instead of a hundred and fifty, and the coast road starts on a shelf.</para>
    ///
    /// <para><b>The tunnel marks the crest. It does not reveal anything, and that is worth writing
    /// down because it was built to.</b> The idea was one frame at the portal holding the strait, the
    /// far shore and the towers together — and the first set of preview shots showed it cannot work.
    /// The crossing is five kilometres from here in a straight line against a 600 m far plane with a
    /// fog wall inside it. Nothing in this world is ever revealed from further than about half a
    /// kilometre, so any set-piece built on distance is one nobody will see. The sea arrives on the
    /// corniche and the bridge arrives on the last corner before it; this is a bore through the top of
    /// a climb, which is what it looks like and enough for it to be.
    /// <see cref="RevealDistance"/> stays published because it is still the frame to photograph and
    /// the place to put a car when the descent is being tuned.</para>
    ///
    /// <para><b>It ends provisionally</b>, the way the Ebental's own end did before
    /// <see cref="StadtfeldCourse"/> closed the ring behind it, and publishes <see cref="EndPoint"/>
    /// and <see cref="EndHeading"/> so <see cref="MeerengeCourse"/> can be grafted onto them rather
    /// than fitted to them.</para>
    /// </summary>
    public static class KalkgratCourse
    {
        /// <summary>
        /// Grade of the climb, percent.
        ///
        /// <para>Six, against the Ebental's 2.6 and the pass's 9.5. The number is chosen from the far
        /// end: the tunnel has to come out high enough that the descent is worth driving, and the climb
        /// has the length it has. Flatten it and the Steilufer is a slope; steepen it and the sweepers
        /// stop being fast, which is the one thing this half of the road is for.</para>
        /// </summary>
        private const float ClimbGrade = 6.6f;

        /// <summary>
        /// Grade of the descent, percent.
        ///
        /// <para>Steeper than the climb and deliberately so — the road gives back in two and a half
        /// kilometres what it took four to gain. Still a percentage point inside the pass, which is the
        /// steepest thing in the world and should stay that way.</para>
        /// </summary>
        private const float DescentGrade = -8.4f;

        /// <summary>Grade through the hairpins. They flatten off, for the reason the pass's do.</summary>
        private const float HairpinGrade = -4f;

        /// <summary>
        /// Straight track before a portal, metres. Comfortably past <c>TunnelBuilder.EndOverhang</c>,
        /// which is how far the massif runs on past the portal ring.
        /// </summary>
        private const float PortalApproach = 60f;

        /// <summary>
        /// Radius of the tightest corner, metres.
        ///
        /// <para>Thirty-eight, and that is the whole argument between this road and the pass. Twenty
        /// metres is a corner you stop for; thirty-eight is one you brake for and then drive out of.
        /// Take it below about thirty and the Steilufer stops being its own road and becomes the
        /// descent from the Passhöhe with the sea painted in behind it.</para>
        /// </summary>
        private const float TightRadius = 38f;

        static KalkgratCourse()
        {
            // Walked once so the end is measured rather than looked up, exactly as the Ebental measures
            // its own. Whatever is grafted on from here follows this road when it is retuned.
            var probe = new RoadCourseBuilder(EbentalCourse.EndPoint, EbentalCourse.EndHeading);

            Append(probe);

            RoadCourse walked = probe.Build();

            EndPoint = walked.ControlPoints[walked.ControlPoints.Count - 1];
            EndHeading = probe.HeadingDegrees;
        }

        /// <summary>Where the road reaches the coast. See the note on the class about what that means.</summary>
        public static Vector3 EndPoint { get; }

        /// <summary>Heading there. 0 faces +Z, increasing turns towards +X.</summary>
        public static float EndHeading { get; }

        /// <summary>
        /// Distance along the course at which the sea first becomes visible — the exit portal of the
        /// Kalkgrattunnel.
        ///
        /// <para>Published rather than kept, because the one picture that decides whether this road
        /// works is taken from here. Measured in the same walk as everything else, so it follows the
        /// climb when the climb is retuned.</para>
        /// </summary>
        public static float RevealDistance { get; private set; }

        /// <summary>
        /// Where the wood on the climb begins and ends along the course, metres.
        ///
        /// <para><b>Two kilometres of dark conifer, and it is here for what comes after it rather than
        /// for itself.</b> This road's whole shape is a climb, a bore, and then the strait — and the one
        /// thing the class note above records is that the reveal was designed for a distance this world
        /// does not have. What it does have is the contrast either side of 280 m of rock, and a wood is
        /// the cheapest way to make the far side of it feel like somewhere else.</para>
        ///
        /// <para><see cref="ForestEnd"/> is hung off <see cref="RevealDistance"/> and therefore follows
        /// the climb when the climb is retuned. It stops short of the portal because the last stretch
        /// wants to be open rock — the belt's exit fade is 400 m, so what the driver gets is a wood
        /// thinning into bare ground and then the bore.</para>
        ///
        /// <para>The road climbs to 163 m against the world's tree line at about 160, so the top of it
        /// is at the line by construction and the belt could not have reached there anyway.</para>
        /// </summary>
        public static float ForestStart => 600f;

        /// <summary>See <see cref="ForestStart"/>.</summary>
        public static float ForestEnd => RevealDistance - 300f;

        /// <summary>The whole course, from the end of the Ebental down to the water.</summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(EbentalCourse.EndPoint, EbentalCourse.EndHeading);

            Append(builder);

            return builder.Build();
        }

        /// <summary>
        /// The shape itself, so the probe in the static constructor and the real walk cannot drift
        /// apart. Two copies of a road are two roads.
        /// </summary>
        private static void Append(RoadCourseBuilder builder)
        {
            // --- Off the end of the Ebental and onto the flank. The road turns left away from the
            // valley it has been following.
            //
            // This instruction used to be the one that decided the world did not close into a ring:
            // straight on was Hochstadt's arterial, 2.4 km further south east, and nothing went there.
            // Something does now. StadtfeldCourse leaves the Ebental 200 m back up it, so the ring runs
            // Talheim - pass - Ebental - Stadtfeld - Hochstadt - motorway, and this road is the branch
            // off that ring rather than the continuation of a corridor. The turn is unchanged; what it
            // means is not.
            //
            // Split 300 + 100 around the station, and the 300 is not a taste. A station's advance sign
            // stands 250 m back up its own road and the search gives up rather than moving closer — so
            // a station inside the first 250 m of a course has nowhere to put one, and the build says
            // so. At 140 m in, which is where this sat first, the sign would have had to stand on the
            // Ebental, which is a different course this one cannot see. Both halves divide by the 10 m
            // point spacing, so the road is unchanged either way.
            builder.Straight(300f, 1.6f);

            // The last fuel before the Kalkgrat, and it is here for the reason Tankstelle Passfuß is
            // where it is. Above this point the road climbs for three and a half kilometres and then
            // falls for two and a half, and there is no straight, level ground on any of it — a
            // forecourt has to be poured flat, and MountainField levels by planting samples that reach
            // a whole verge width in every direction. On a 6 % climb that is a shelf standing two
            // metres proud of the carriageway it serves.
            builder.AddFuelStation("Tankstelle Kalkgratfuß", -1f);
            builder.Straight(100f, 1.6f);

            builder.Turn(420f, -30f, 3f);
            builder.Straight(260f, ClimbGrade);
            builder.Turn(380f, -26f, ClimbGrade);
            builder.Straight(240f, ClimbGrade);

            // Half way up, where the whole of the Ebental is still behind you and the crest is not yet
            // in the way. The only place on this road that looks back rather than forward.
            builder.AddViewpoint("Ebentalblick");

            builder.Turn(300f, 34f, ClimbGrade);
            builder.Straight(200f, ClimbGrade);
            builder.Turn(450f, -38f, ClimbGrade);
            builder.Straight(260f, ClimbGrade);
            builder.Turn(340f, 28f, ClimbGrade);
            builder.Straight(280f, ClimbGrade);

            // --- The crest. The climb eases before the portal rather than at it, because a road that is
            // still doing 6 % where the rock starts drags the massif's own footing uphill with it.
            builder.Turn(400f, -32f, 4.2f);
            builder.Straight(240f, 3f);

            builder.Straight(PortalApproach, 1.4f);

            float tunnelStart = builder.Distance;
            builder.Straight(280f, 1f);
            builder.AddFeature(RoadFeatureKind.Tunnel, tunnelStart, builder.Distance, "Kalkgrattunnel");

            builder.Straight(PortalApproach, -1.4f);

            // Out of the rock and straight onto the top of the descent. See the class note for what
            // this viewpoint does and does not show — the sea is five kilometres away and the fog is at
            // six hundred metres, so what is in frame here is the Steilufer falling away.
            RevealDistance = builder.Distance;
            builder.AddViewpoint("Steilkanzel Nord");

            // --- Down the Steilufer. The hairpins alternate, so the stack rotates down the face rather
            // than spiralling into itself — the same rule the pass's switchbacks obey and for the same
            // reason: the terrain takes its height from the nearest carriageway, and two legs stacked
            // in plan bury the lower one.
            builder.Turn(60f, -104f, HairpinGrade);
            builder.Straight(130f, DescentGrade);
            builder.Turn(45f, 128f, HairpinGrade);
            builder.Straight(110f, DescentGrade);

            // Roofed on the uphill side and open to the drop, which is the one thing a gallery is for.
            // On a straight, like every portal on this road.
            float galleryStart = builder.Distance;
            builder.Straight(120f, DescentGrade);
            builder.AddFeature(RoadFeatureKind.Gallery, galleryStart, builder.Distance, "Klippengalerie");

            builder.Straight(90f, DescentGrade);
            builder.Turn(TightRadius, -136f, HairpinGrade);
            builder.Straight(100f, DescentGrade);

            // Level across the span, for the reason AutobahnCourse gives at its own viaducts: the deck,
            // the piers and the parapet are all measured off the carriageway, and a grade across the
            // span is one more thing to be wrong about before the first one is right.
            float bridgeStart = builder.Distance;
            builder.Straight(180f, 0f);
            builder.AddFeature(RoadFeatureKind.Bridge, bridgeStart, builder.Distance, "Schluchtbrücke");

            builder.Straight(90f, DescentGrade);
            builder.Turn(52f, 122f, HairpinGrade);
            builder.Straight(140f, DescentGrade);
            builder.Turn(70f, -110f, HairpinGrade);
            builder.Straight(120f, DescentGrade);

            // The last corner of the descent, where the road comes back down to the level the coast
            // road runs at.
            builder.AddViewpoint("Steilkanzel");

            // --- Run-out along the shore. The only straight, near-level ground between the crest and
            // the coast road, which is why the second station is here and not anywhere better.
            builder.Turn(160f, 46f, -3f);
            builder.Straight(120f, -1.6f);
            builder.AddFuelStation("Tankstelle Steilufer", -1f);
            builder.Straight(140f, -1.6f);
        }
    }
}
