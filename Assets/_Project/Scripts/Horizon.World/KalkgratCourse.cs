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
    /// on 20 m hairpins taken at walking pace; nothing here is under 38 m. That is fast enough to
    /// carry speed between the corners and tight enough that they have to be braked for — and where
    /// the pass has a mountain on the outside of every bend, this has a drop. The gallery and the
    /// ravine viaduct are there because the road genuinely has nowhere else to go, not as decoration.</para>
    ///
    /// <para><b>The tunnel is a shutter, not a feature.</b> Everything this road is built for is on the
    /// far side of the Kalkgrat — the water, the far shore, and the towers standing in it. Two hundred
    /// and eighty metres of rock means none of it is visible until the portal, and then all of it is
    /// at once. <see cref="RevealDistance"/> is where that happens, and
    /// <c>WorldPreviewRenderer</c> photographs it, because if the reveal does not read from the
    /// driver's seat then the span or the fog is wrong and no other picture will say so.</para>
    ///
    /// <para><b>It ends provisionally</b>, the way the Ebental's own end does, and publishes
    /// <see cref="EndPoint"/> and <see cref="EndHeading"/> so <see cref="MeerengeCourse"/> can be
    /// grafted onto them rather than fitted to them.</para>
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
            // valley it has been following; straight on is Hochstadt's arterial, 2.4 km further south
            // east, and this is the one instruction that decides the world does not simply close into
            // a ring here.
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

            // Everything on the far side of the ridge arrives here, in one frame. See the class note.
            RevealDistance = builder.Distance;
            builder.AddViewpoint("Meerkanzel");

            // --- Down the Steilufer. The hairpins alternate, so the stack rotates down the face rather
            // than spiralling into itself — the same rule the pass's switchbacks obey and for the same
            // reason: the terrain takes its height from the nearest carriageway, and two legs stacked
            // in plan bury the lower one.
            builder.Turn(60f, -104f, HairpinGrade);
            builder.Straight(220f, DescentGrade);
            builder.Turn(45f, 128f, HairpinGrade);
            builder.Straight(180f, DescentGrade);

            // Roofed on the uphill side and open to the drop, which is the one thing a gallery is for.
            // On a straight, like every portal on this road.
            float galleryStart = builder.Distance;
            builder.Straight(120f, DescentGrade);
            builder.AddFeature(RoadFeatureKind.Gallery, galleryStart, builder.Distance, "Klippengalerie");

            builder.Straight(140f, DescentGrade);
            builder.Turn(TightRadius, -136f, HairpinGrade);
            builder.Straight(160f, DescentGrade);

            // Level across the span, for the reason AutobahnCourse gives at its own viaducts: the deck,
            // the piers and the parapet are all measured off the carriageway, and a grade across the
            // span is one more thing to be wrong about before the first one is right.
            float bridgeStart = builder.Distance;
            builder.Straight(180f, 0f);
            builder.AddFeature(RoadFeatureKind.Bridge, bridgeStart, builder.Distance, "Schluchtbrücke");

            builder.Straight(140f, DescentGrade);
            builder.Turn(52f, 122f, HairpinGrade);
            builder.Straight(240f, DescentGrade);
            builder.Turn(70f, -110f, HairpinGrade);
            builder.Straight(200f, DescentGrade);

            // The last corner of the descent, and the first place the coast is seen from sea level
            // rather than from above it.
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
