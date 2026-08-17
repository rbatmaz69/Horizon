using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The signal head at one junction approach, and the paint that goes with it.
    ///
    /// <para><b>Near-side, pole-mounted, no arm over the carriageway.</b> A gantry head reads better
    /// from a hundred metres and is the wrong shape for this junction: the boulevard's approaches are
    /// eight metres of carriageway, the player meets a light from thirty metres at 40 km/h, and an arm
    /// is a dozen more triangles and a cantilever to get wrong on every bowed street in the grid. A mast
    /// at the stop line is what a German junction of this size actually has.</para>
    ///
    /// <para>Everything about where it stands is <b>measured off the street it belongs to</b> rather
    /// than written down — the trim point where the ribbon stops, the footway's own height, the driver's
    /// right hand. That is the same doctrine the junction pads follow, and it is what keeps a mast on
    /// the pavement when somebody widens a boulevard.</para>
    /// </summary>
    public static class TrafficSignalMeshes
    {
        /// <summary>Height of the top of the mast above the footway, metres.</summary>
        private const float PoleHeight = 3.2f;

        /// <summary>Side of the square mast, metres.</summary>
        private const float PoleSide = 0.13f;

        /// <summary>The head: how wide across the mast, how deep along it, how tall.</summary>
        private const float HeadWidth = 0.32f;

        private const float HeadDepth = 0.22f;

        private const float HeadHeight = 0.86f;

        /// <summary>Height of the bottom of the head above the footway.</summary>
        private const float HeadBase = 2.24f;

        /// <summary>Side of one lens, metres, and how far its centres are apart.</summary>
        private const float LensSize = 0.20f;

        private const float LensPitch = 0.26f;

        /// <summary>How far the lens sits proud of the head's face, so it never z-fights with it.</summary>
        private const float LensProud = 0.012f;

        /// <summary>
        /// How far behind the kerb face the mast stands, metres.
        ///
        /// <para>Measured from the <i>kerb</i>, not from the back of the paving, and the difference is
        /// the whole legibility of the thing. Inset from the outer edge put it 11.9 m from the centreline
        /// on a boulevard — right at the back of a four-and-a-half-metre footway, behind the street trees,
        /// where a driver in the nearside lane would be looking over their shoulder for it. A signal
        /// stands at the kerb it controls.</para>
        /// </summary>
        private const float PoleSetBack = 0.55f;

        /// <summary>
        /// How far back from the junction the stop line is painted, metres.
        ///
        /// <para>Far enough that the crossing clears it by about two metres. At 3.6 the two were three
        /// hundred millimetres apart and read as one wide smear of paint rather than as a line you stop
        /// at and a crossing you stop short of.</para>
        /// </summary>
        private const float StopLineSetback = 5.4f;

        /// <summary>And how far back from the junction the crossing beyond it starts.</summary>
        private const float CrossingSetback = 0.7f;

        /// <summary>How many submeshes the lens buffer needs: three lenses for each phase group.</summary>
        public static int LensSubmeshCount => TrafficSignalPlan.Groups * 3;

        /// <summary>Which lens submesh a group's red, amber or green quads go in.</summary>
        public static int LensSubmesh(int group, TrafficSignalState state)
        {
            return group * 3 + (int)state;
        }

        /// <summary>
        /// One approach: the mast and head into the street buffer, the three lenses into the lens
        /// buffer, and the stop line and crossing onto the carriageway.
        ///
        /// <para>All three together because they share one number — where along the street the junction
        /// begins. Splitting them would mean deriving that trim point twice and having the paint and the
        /// mast disagree by a few centimetres, which is exactly the class of thing
        /// <c>StreetJunctionBuilder</c> keeps its corners in one place to avoid.</para>
        /// </summary>
        /// <param name="viewFrom">
        /// Where a driver meets this signal: on the carriageway, back up the approach, at eye height.
        /// Handed back so the setup tool can put a preview station there — the night render is the only
        /// way to see whether the lenses light, and a station aimed at a coordinate somebody typed is a
        /// station that quietly stops pointing at anything.
        /// </param>
        /// <param name="viewAt">And what it should be looking at: the head itself.</param>
        public static void AppendApproach(
            StreetNetwork network,
            int node,
            int edge,
            int group,
            VegetationMeshBuffer street,
            VegetationMeshBuffer lenses,
            out Vector3 viewFrom,
            out Vector3 viewAt)
        {
            viewFrom = Vector3.zero;
            viewAt = Vector3.zero;

            StreetEdge approach = network.Edges[edge];
            if (approach.Path == null)
            {
                return;
            }

            // Which way a car on this approach is travelling. A lane that ends at this node either runs
            // with the path — when the node is the edge's far end — or against it.
            bool withPath = approach.ToNode == node;

            // Where the ribbon stops and the junction pad takes over, as a distance along the path.
            float junction = withPath
                ? approach.Length - approach.TrimEnd
                : approach.TrimStart;

            // Into the street, away from the junction.
            float into = withPath ? -1f : 1f;

            TownStreetShape shape = approach.Shape;

            AppendPaint(approach, shape, junction, into, withPath, street);
            AppendHead(approach, shape, junction, into, withPath, group, street, lenses,
                out viewFrom, out viewAt);
        }

        /// <summary>The stop line, and the crossing between it and the junction.</summary>
        private static void AppendPaint(
            StreetEdge edge,
            in TownStreetShape shape,
            float junction,
            float into,
            bool withPath,
            VegetationMeshBuffer street)
        {
            float stopAt = junction + into * StopLineSetback;

            // Painted the way the car meets it: the leading edge of the bar is the thing you stop at, so
            // the bar runs from there away from the junction rather than towards it.
            TownStreetBuilder.AppendStopLine(
                edge.Path, shape, Mathf.Min(stopAt, stopAt + into * TownStreetBuilder.StopLineWidth),
                withPath, street);

            // The crossing sits between the stop line and the junction, which is the order a driver meets
            // them in and the reason the stop line is set back as far as it is: the setback is the
            // crossing's own depth plus the room to stand clear of it.
            float crossingNear = junction + into * CrossingSetback;
            float crossingFar = crossingNear + into * TownStreetBuilder.CrossingDepth;

            TownStreetBuilder.AppendCrossing(
                edge.Path, shape, Mathf.Min(crossingNear, crossingFar),
                Mathf.Max(crossingNear, crossingFar), street);
        }

        /// <summary>The mast, the head box and the three lenses.</summary>
        private static void AppendHead(
            StreetEdge edge,
            in TownStreetShape shape,
            float junction,
            float into,
            bool withPath,
            int group,
            VegetationMeshBuffer street,
            VegetationMeshBuffer lenses,
            out Vector3 viewFrom,
            out Vector3 viewAt)
        {
            float standAt = Mathf.Clamp(
                junction + into * StopLineSetback, 0f, edge.Path.Length);

            // The driver's own frame: forward is the way the car is going, right is its right hand, and
            // both flip together for the lane that runs against the path. Taking one from the geometry
            // and the other from the direction of travel is how a mast ends up in the oncoming lane.
            Vector3 forward = edge.Path.GetDirectionAtDistance(standAt);
            Vector3 right = edge.Path.GetRightAtDistance(standAt);

            if (!withPath)
            {
                forward = -forward;
                right = -right;
            }

            forward.y = 0f;
            right.y = 0f;
            forward = forward.sqrMagnitude > 0.000001f ? forward.normalized : Vector3.forward;
            right = right.sqrMagnitude > 0.000001f ? right.normalized : Vector3.right;

            // On the footway on the driver's right, at the level the kerb top is built at — the same
            // number the ribbon's own cross-section uses, so the mast stands on the pavement rather
            // than in it.
            float across = (shape.HalfWidth + shape.KerbFace + PoleSetBack) * (withPath ? 1f : -1f);
            Vector3 foot = TownStreetBuilder.PointAcross(
                edge.Path, shape, standAt, across, shape.SurfaceLift + shape.KerbHeight);

            AppendBox(street, TownStreetBuilder.SignalBodySubmesh,
                foot + Vector3.up * (PoleHeight * 0.5f),
                right, forward, Vector3.up,
                PoleSide, PoleSide, PoleHeight);

            // The head hangs on the side of the mast that faces the traffic, so its centre is half a
            // mast and half a head further back down the street.
            Vector3 headCentre = foot
                                 + Vector3.up * (HeadBase + HeadHeight * 0.5f)
                                 - forward * ((PoleSide + HeadDepth) * 0.5f);

            AppendBox(street, TownStreetBuilder.SignalBodySubmesh,
                headCentre, right, forward, Vector3.up,
                HeadWidth, HeadDepth, HeadHeight);

            // Red on top, then amber, then green — the order everybody reads without being told, and the
            // reason a signal is still legible to somebody who cannot tell the colours apart.
            Vector3 face = headCentre - forward * (HeadDepth * 0.5f + LensProud);

            for (int i = 0; i < 3; i++)
            {
                // i counts down the head, and TrafficSignalState counts up from Red — so the two agree,
                // and red lands on top. Written as (2 - i) first, which put green at the top of every
                // signal in the city: legible only if you read the colour, which is the one thing a
                // signal is not allowed to depend on.
                var state = (TrafficSignalState)i;
                Vector3 centre = face + Vector3.up * ((1 - i) * LensPitch);

                AppendFacingQuad(lenses, LensSubmesh(group, state), centre, right, -forward, LensSize);
            }

            // A driver's view of this head: in the middle of the lane the signal governs, far enough back
            // to have the stop line and the crossing in shot as well.
            viewFrom = TownStreetBuilder.PointAcross(
                edge.Path, shape,
                Mathf.Clamp(standAt + into * ViewSetback, 0f, edge.Path.Length),
                shape.HalfWidth * 0.5f * (withPath ? 1f : -1f),
                shape.SurfaceLift + ViewHeight);

            viewAt = headCentre;
        }

        /// <summary>How far back down the approach the preview station stands, metres.</summary>
        private const float ViewSetback = 17f;

        /// <summary>And at what height above the carriageway — roughly a driver's eyes.</summary>
        private const float ViewHeight = 1.35f;

        /// <summary>A box, given its centre and its three axes. Six quads, outward normals.</summary>
        private static void AppendBox(
            VegetationMeshBuffer into,
            int submesh,
            Vector3 centre,
            Vector3 right,
            Vector3 forward,
            Vector3 up,
            float width,
            float depth,
            float height)
        {
            Vector3 x = right * (width * 0.5f);
            Vector3 z = forward * (depth * 0.5f);
            Vector3 y = up * (height * 0.5f);

            // Four sides and a top. No bottom: it is either underground or under a head, and a face
            // nobody can see is two triangles in every town tile that draws one.
            AddFace(into, submesh, centre + z, x, y, forward);
            AddFace(into, submesh, centre - z, -x, y, -forward);
            AddFace(into, submesh, centre + x, -z, y, right);
            AddFace(into, submesh, centre - x, z, y, -right);
            AddFace(into, submesh, centre + y, x, z, up);
        }

        /// <summary>One rectangle about a centre, spanned by two half-axes and facing a direction.</summary>
        private static void AddFace(
            VegetationMeshBuffer into, int submesh, Vector3 centre, Vector3 u, Vector3 v, Vector3 outward)
        {
            into.AddQuadFacing(
                submesh,
                centre - u - v, centre + u - v, centre + u + v, centre - u + v,
                outward);
        }

        /// <summary>A square lens on the front of a head.</summary>
        private static void AppendFacingQuad(
            VegetationMeshBuffer into, int submesh, Vector3 centre, Vector3 right, Vector3 outward,
            float size)
        {
            Vector3 u = right * (size * 0.5f);
            Vector3 v = Vector3.up * (size * 0.5f);

            AddFace(into, submesh, centre, u, v, outward);
        }
    }
}
