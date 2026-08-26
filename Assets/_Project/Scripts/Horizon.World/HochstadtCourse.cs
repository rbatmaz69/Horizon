using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The city's arterial: the boulevard the motorway becomes, and the axis Hochstadt is written
    /// against.
    ///
    /// <para><b>Straight, and that is the entire design.</b> A town is authored in metres along and
    /// across its trunk road (<see cref="TownShape.ToWorld"/>), and that mapping folds where the road
    /// bends towards the town: a point <c>d</c> out on the inside of a bend of radius <c>R</c> has its
    /// along-axis squeezed by <c>1 - d/R</c>, so <c>LimitAcross</c> refuses anything past
    /// <c>0.65·R</c>. Talheim is 350 m wide and needs a 540 m radius; a city 1 km across would need
    /// 1500 m, and the motorway's tightest bend is 731.</para>
    ///
    /// <para>On a straight the curvature is zero, the limit is infinite, and the city may be as wide as
    /// it likes. So rather than inventing a second coordinate system for large towns — which was the
    /// first plan — the city gets a road worth hanging a city on. Nothing in <c>TownShape</c>,
    /// <c>StreetNetwork</c> or <c>TownPlanner</c> changes for it, and Talheim is untouched.</para>
    ///
    /// <para>It is also, conveniently, what the place should look like: an arrival road that runs dead
    /// straight into the middle of a city, with the grid hung off it.</para>
    /// </summary>
    public static class HochstadtCourse
    {
        /// <summary>
        /// Length of the arterial, metres — and therefore the city's along-axis.
        ///
        /// <para>1300 for a city about a kilometre deep, with room at each end: the gateway where the
        /// motorway hands over, and a tail past the far edge so the last cross street still has road to
        /// hang off. <c>TownShape.AlongStart/AlongEnd</c> sit inside this, not on it.</para>
        /// </summary>
        public const float Length = 1300f;

        /// <summary>
        /// Fall along the arterial, percent.
        ///
        /// <para>Slight and downhill, so the skyline is seen from slightly above on the way in and the
        /// city sits in a shallow bowl rather than on a table. Every height in the city is derived from
        /// this line by <c>TownShape.FloorHeight</c>, so this number tilts the whole place.</para>
        /// </summary>
        public const float Grade = -0.35f;

        /// <summary>
        /// Where the city's streets may reach along the arterial.
        ///
        /// <para>Inset from both ends. At the near end because the first 140 m is the motorway handing
        /// over and no cross street should meet that; at the far end because <c>TownShape</c>'s basin
        /// and its skirt rings reach past the last street and need arterial to sit against.</para>
        /// </summary>
        public const float CityStart = 0f;

        /// <summary>See <see cref="CityStart"/>.</summary>
        public const float CityEnd = 1180f;

        /// <summary>
        /// Half the city's width, metres. Unbounded by the mapping because the arterial is straight —
        /// this is a decision about how big the place should be, not a limit.
        /// </summary>
        public const float HalfWidth = 520f;

        static HochstadtCourse()
        {
            // Walked once so the east gate is measured rather than looked up, exactly as the Ebental
            // measures its fork. Trivial here — the arterial is one straight — and done this way anyway,
            // because what hangs off it has to follow this road when Length, Grade or CityEnd is
            // retuned rather than being typed beside them.
            var probe = new RoadCourseBuilder(AutobahnCourse.EndPoint, AutobahnCourse.EndHeading);

            AppendToGate(probe);

            EastGatePoint = probe.Position;
            EastGateHeading = probe.HeadingDegrees;

            AppendFromGate(probe);
        }

        /// <summary>
        /// The far end of the boulevard — where the road out of Hochstadt starts, and the point
        /// <see cref="StadtfeldCourse"/> is grafted onto.
        ///
        /// <para><b>At <see cref="CityEnd"/>, not at <see cref="Length"/>, and the difference is the
        /// whole point.</b> This course is never paved: <c>PrototypeSetup</c> builds it as
        /// <c>ArterialPath</c> and hands it straight to <c>PrepareTown</c>, because a town's trunk road
        /// only has to be a coordinate frame and a height datum. What a driver is actually on through
        /// the city is the <c>Boulevard</c> in <c>HochstadtLayout</c>, and that ends at a degree-one
        /// node at <see cref="CityEnd"/> with 120 m of bare datum beyond it for the skirt rings to sit
        /// against. A road grafted onto the arterial's <i>end</i> would therefore start 120 m out in a
        /// field with nothing between it and the city — a threshold with nothing behind it, which is
        /// the fault the Meerenge's own remarks record paying for once already.</para>
        /// </summary>
        public static Vector3 EastGatePoint { get; }

        /// <summary>
        /// Heading at the gate, <b>facing out of the city</b> — the way the road on leaves. Reverse it
        /// for the way a driver arrives. 0 faces +Z, increasing turns towards +X.
        /// </summary>
        public static float EastGateHeading { get; }

        /// <summary>
        /// Builds the arterial, grafted onto the far end of the motorway.
        ///
        /// <para>No probe-and-graft solve here, unlike <see cref="AutobahnCourse"/> and
        /// <see cref="MountainPassCourse"/>: those two have to work out what start puts their *end*
        /// somewhere fixed. This one starts where the motorway finishes and runs on, so the answer is
        /// simply the motorway's end.</para>
        /// </summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(AutobahnCourse.EndPoint, AutobahnCourse.EndHeading);

            AppendToGate(builder);
            AppendFromGate(builder);

            return builder.Build();
        }

        /// <summary>
        /// The arterial as far as the east gate. Split out of one <c>Straight(Length)</c> so the gate's
        /// pose comes from the walk rather than from arithmetic beside it; <see cref="CityEnd"/> and the
        /// remaining 120 m both divide by <c>RoadCourseBuilder</c>'s 10 m point spacing, so the line is
        /// unchanged to the millimetre.
        /// </summary>
        private static void AppendToGate(RoadCourseBuilder builder)
        {
            builder.Straight(CityEnd, Grade);
        }

        /// <summary>
        /// The datum tail past the last cross street. Never driven — see <see cref="EastGatePoint"/>
        /// for what is, and why.
        /// </summary>
        private static void AppendFromGate(RoadCourseBuilder builder)
        {
            builder.Straight(Length - CityEnd, Grade);
        }
    }
}
