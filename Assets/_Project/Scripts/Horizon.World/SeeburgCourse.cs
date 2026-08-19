using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Seeburg's axis: the line the harbour town is written against, running along the shore.
    ///
    /// <para><b>Straight, for the same reason Hochstadt's arterial is.</b> A town is authored in metres
    /// along and across its trunk road (<see cref="TownShape.ToWorld"/>), and that mapping folds where
    /// the road bends towards the town: <c>LimitAcross</c> refuses anything past <c>0.65·R</c>. The
    /// coast road's bends are 320 m, which would allow a town 208 m deep; Seeburg is 380. So the town
    /// gets an axis of its own rather than hanging off the road that arrives at it.</para>
    ///
    /// <para><b>Across the coast road, not along it — and that is the difference from Hochstadt.</b>
    /// The motorway runs <i>into</i> Hochstadt and becomes its boulevard, so the arterial simply carries
    /// on where the motorway stopped. Here the road arrives at the water and the town runs along the
    /// water, so the axis crosses it at a right angle. <see cref="CoastCourse.EndHeading"/> faces the
    /// sea; turn it ninety degrees and you have the shoreline.</para>
    ///
    /// <para><b>Which ninety degrees matters.</b> <c>ToWorld</c> puts positive <c>across</c> to the
    /// road's right when <c>TownSide</c> is +1. Turning <i>right</i> off the coast road's heading leaves
    /// the axis' own right pointing back inland, so positive across is land and negative across is
    /// water. Turning the other way would build the town in the sea, quietly and with every check
    /// passing.</para>
    ///
    /// <para><b>Grafted 200 m back along itself.</b> Starting the axis at the coast road's end would put
    /// the arrival at the corner of the town rather than at its harbour — you would come down out of the
    /// mountains and find the place beside you instead of in front of you. Offsetting the start puts the
    /// junction at <see cref="GatewayAlong"/>, with the old town on one hand and the promenade on the
    /// other.</para>
    ///
    /// <para>Like Hochstadt's arterial this line is never paved. It is a coordinate system and a height
    /// datum; what is driven is the waterfront boulevard in <see cref="SeeburgLayout"/>, which sits on
    /// it at <c>across = 0</c>.</para>
    /// </summary>
    public static class SeeburgCourse
    {
        /// <summary>
        /// Length of the axis, metres.
        ///
        /// <para>Sixty metres past <see cref="CityEnd"/>, because <c>TownShape</c>'s basin and its two
        /// skirt rings reach beyond the last street and need axis to sit against — the same tail
        /// <c>HochstadtCourse</c> leaves.</para>
        /// </summary>
        public const float Length = 760f;

        /// <summary>Where the town's streets may reach along the axis.</summary>
        public const float CityStart = 0f;

        /// <summary>See <see cref="CityStart"/>.</summary>
        public const float CityEnd = 700f;

        /// <summary>
        /// Where the coast road lands on the axis, metres along.
        ///
        /// <para>A third of the way in rather than the middle. The old town wants to be the short side —
        /// a dense quarter reads as old because you get through it quickly — and the promenade wants the
        /// long one, because a seafront that ends after two hundred metres is a car park.</para>
        /// </summary>
        public const float GatewayAlong = 200f;

        /// <summary>How far the town reaches inland from the axis, metres.</summary>
        public const float Inland = 380f;

        /// <summary>
        /// How far the town's levelled basin reaches seaward of the axis, metres.
        ///
        /// <para><b>Far wider than anything is built on, and that is the whole job of it.</b> Only the
        /// first thirty metres carry a promenade; the rest is flat ground that ends up under water. It
        /// has to be there because of the order <c>MountainField.HeightAt</c> works in: the level shelf
        /// blends out to the natural hillside over <c>BlendDistance</c>, and the natural ground out here
        /// is at −56 to −72 m while the town floor is near −45. Let the shoreline fall outside the
        /// levelled apron and the ground between the two drops fifteen metres below a sea surface that
        /// does not reach it — a dry trench at the beach, which every check in the build is happy
        /// with.</para>
        ///
        /// <para>Two hundred metres puts the waterline inside levelled ground along the whole town, with
        /// room for the shore to recede at the far end (see <see cref="SeaRadius"/>) and for the harbour
        /// basin to be dug without ever reaching the edge of the shelf. Level samples are cheap; the
        /// trench is not.</para>
        /// </summary>
        public const float Seaward = 200f;

        /// <summary>
        /// Fall along the axis, percent. Nearly nothing — this is a seafront.
        ///
        /// <para>It is not zero because a dead level kilometre of waterfront reads as a drawing rather
        /// than a place. One and a half metres over the length is under the eye's threshold for a slope
        /// and above its threshold for a plane.</para>
        /// </summary>
        private const float Grade = -0.2f;

        /// <summary>
        /// How far seaward of the axis the waterline sits at the gateway, metres.
        ///
        /// <para>Everything between is promenade and beach. It must stay well inside
        /// <see cref="Seaward"/> — at the gateway and, more to the point, at the far end of town where
        /// the disc's rim has receded another fifty metres.</para>
        ///
        /// <para><b>Forty, not sixty.</b> At sixty the promenade looked out over a hundred metres of
        /// flat, empty, levelled ground before the water started — from the seafront the town read as a
        /// car park with a sea behind it. Forty puts the beach within sight of the rail and still leaves
        /// seven metres of flat between the boulevard's kerb and the top of the bank, which is what stops
        /// the paving standing on a plinth.</para>
        /// </summary>
        public const float ShoreOffset = 40f;

        /// <summary>
        /// Radius of the Westmeer, metres.
        ///
        /// <para><b>Large, so the shoreline is nearly a straight line along the town.</b> The sea is a
        /// disc rather than a drawn coastline — nobody can tell, because the water is opaque and the fog
        /// wall stands inside the far plane. But a small disc has a rim that curves visibly over seven
        /// hundred metres of waterfront: at 1 km the water is sixty metres out at the harbour and two
        /// hundred and sixty at the far end of town, and every metre of that difference is levelled
        /// apron that has to be paid for. At 2.6 km it recedes by fifty over the whole front.</para>
        ///
        /// <para>The reason this was not simply set large to begin with is
        /// <see cref="SeaBedScale"/>.</para>
        /// </summary>
        public const float SeaRadius = 2600f;

        /// <summary>
        /// How far in from the shore the sea reaches its full depth, metres.
        ///
        /// <para><b>This is what makes a large disc possible at all.</b> The bed is a cosine dish and the
        /// tile builder reads the carved bed back to decide how dark the water is, so the dish's scale
        /// <i>is</i> the shading. Tied to the radius — which is what <c>WaterBody</c> did before there
        /// was a reason not to — a 2.6 km disc is uniformly pale: the first six hundred metres off the
        /// beach, which is all anyone ever sees of it, never get deeper than a puddle looks. Four
        /// hundred and fifty metres puts the water properly dark by the time it is halfway to the fog
        /// wall, and leaves the rim shallow enough to read as a beach.</para>
        /// </summary>
        public const float SeaBedScale = 450f;

        /// <summary>Depth of the Westmeer at its darkest, metres.</summary>
        public const float SeaDepth = 9f;

        /// <summary>
        /// Width of the beach, metres — the band outside the disc where the ground is drawn down to the
        /// waterline.
        ///
        /// <para>Narrower than the seventy metres this coast had when the road ran onto it, because the
        /// bank pulls terrain <i>down</i> towards the water and it now has a promenade to stop short of.
        /// At twenty it reaches to twenty metres out from the boulevard's centreline, seven clear of its
        /// kerb — a beach the promenade looks along rather than one it stands in.</para>
        /// </summary>
        public const float SeaBankEase = 20f;

        /// <summary>How far the sea's surface sits below the waterfront, metres.</summary>
        public const float SeaFreeboard = 3.5f;

        /// <summary>
        /// The harbour basin, in town-local terms: where its centre sits and how big it is.
        ///
        /// <para>Dug so that its landward rim stands thirty metres out from the axis — far enough that
        /// the boulevard's kerb and a quayside apron fit between, close enough that the water is the
        /// thing you are looking at from the promenade. Placed a little along from the gateway rather
        /// than straight in front of it, so the junction throat and the quay are not competing for the
        /// same ground.</para>
        /// </summary>
        public const float BasinAlong = GatewayAlong + 80f;

        /// <summary>See <see cref="BasinAlong"/>.</summary>
        public const float BasinAcross = -160f;

        /// <summary>See <see cref="BasinAlong"/>.</summary>
        public const float BasinRadius = 130f;

        /// <summary>
        /// Depth of the basin, metres, and the width of its bank.
        ///
        /// <para>The bank is almost nothing because a quay is a vertical wall: what is wanted from the
        /// terrain here is one cell of steep, with the quay geometry standing over the seam. A proper
        /// eased bank would be a beach inside the harbour.</para>
        /// </summary>
        public const float BasinDepth = 6f;

        /// <summary>See <see cref="BasinDepth"/>.</summary>
        public const float BasinBankEase = 6f;

        /// <summary>
        /// How far inland of the axis the coast road hands over, metres — the back of the town.
        ///
        /// <para><b>At the town's edge, not at its waterfront, and the first attempt had this
        /// backwards.</b> Ending the road one block from the water put its last three hundred metres
        /// straight through the middle of Seeburg: the coast road runs inland-to-seaward at this
        /// station, so everything between the handover and the town boundary is a road ribbon crossing
        /// the second row, the third row and the back lane without a junction at any of them. It carved
        /// its own shelf through the levelled basin as well — the ground report came back forty-five
        /// percent steep with sixteen metres of wander in it.</para>
        ///
        /// <para>So the mountain road stops where the town starts, and what carries on is a street: the
        /// spine at <see cref="GatewayAlong"/> runs from here down to the boulevard, crossing every row
        /// on the way at a junction the network built. That is also the better arrival — you come off
        /// the pass, the road becomes a street, and the sea is at the end of it.</para>
        /// </summary>
        public const float GatewayAcross = 352f;

        /// <summary>
        /// The town's cross-fall, declared here rather than in <c>TownShape.Seeburg</c>.
        ///
        /// <para><b>Because the axis' own height depends on it.</b> The coast road hands over
        /// <see cref="GatewayAcross"/> metres out from the axis, where the town's floor already stands
        /// <see cref="FloorRiseAt"/> above the axis itself — so the axis has to be set that much lower
        /// for the two to meet. Reading the figure out of the shape preset would make the preset depend
        /// on the course and the course on the preset; declaring it at the bottom of the stack and
        /// having the preset read it leaves one source of truth and no cycle.</para>
        /// </summary>
        public const float CrossFallNear = 0.010f;

        /// <summary>See <see cref="CrossFallNear"/>.</summary>
        public const float CrossFallFar = 0.018f;

        /// <summary>See <see cref="CrossFallNear"/>.</summary>
        public const float CrossFallBreak = 120f;

        /// <summary>
        /// How far the town's floor stands above the axis at <paramref name="across"/> metres out.
        ///
        /// <para>The same two-slope rule <c>TownShape.CrossFall</c> applies, and it has to stay the same
        /// one. There is no dish term because Seeburg has no dish — see <c>TownShape.Seeburg</c> — which
        /// is what makes this exact rather than nearly right.</para>
        /// </summary>
        public static float FloorRiseAt(float across)
        {
            float distance = Mathf.Abs(across);
            return Mathf.Min(distance, CrossFallBreak) * CrossFallNear
                   + Mathf.Max(0f, distance - CrossFallBreak) * CrossFallFar;
        }

        static SeeburgCourse()
        {
            StartHeading = CoastCourse.EndHeading + 90f;

            Vector3 alongDirection = Quaternion.Euler(0f, StartHeading, 0f) * Vector3.forward;

            // The axis' own right, which is back inland — see the class remarks for why that particular
            // ninety degrees.
            Vector3 inlandDirection = Quaternion.Euler(0f, StartHeading + 90f, 0f) * Vector3.forward;

            Vector3 start = CoastCourse.EndPoint
                            - alongDirection * GatewayAlong
                            - inlandDirection * GatewayAcross;

            // Set so the town's *floor* at the handover — not the axis, which is GatewayAcross metres
            // away and that much lower — comes out at exactly the height the coast road arrives at. Two
            // terms: the cross-fall out to the gateway, and whatever the axis will have fallen by the
            // time it gets there.
            start.y = CoastCourse.EndPoint.y
                      - FloorRiseAt(GatewayAcross)
                      - GatewayAlong * Grade * 0.01f;

            StartPoint = start;
        }

        /// <summary>Where the axis begins — the old town end, inland of the beach.</summary>
        public static Vector3 StartPoint { get; }

        /// <summary>Heading there. 0 faces +Z, increasing turns towards +X.</summary>
        public static float StartHeading { get; }

        /// <summary>The axis. One straight, because that is the whole point of it.</summary>
        public static RoadCourse Build()
        {
            var builder = new RoadCourseBuilder(StartPoint, StartHeading);
            builder.Straight(Length, Grade);

            return builder.Build();
        }
    }
}
