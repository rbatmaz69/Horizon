using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Timber utility poles and the wire slung between them.
    ///
    /// <para><b>This is the strongest sense of distance per triangle anything in this project can
    /// buy.</b> A pole is thirty triangles. What it does is give the middle distance a row of objects of
    /// *known* size at *known* spacing, receding — which is the one cue that turns a hillside into a
    /// hillside a mile wide rather than a wall forty metres off. The delineator posts already do this
    /// for the near field and their own remarks say why it mattered there; this is the same argument at
    /// ten times the range, where the posts are already a blur.</para>
    ///
    /// <para><b>The wire is what makes it read, and it is why this is not just a row of posts.</b> Two
    /// poles are two objects; two poles with a line sagging between them are a single structure crossing
    /// ground, and the eye reads the sag as depth without being told. It costs four quads.</para>
    ///
    /// <para>Everything lands in <c>PlantMeshes.BarkSubmesh</c> — creosoted timber is close enough to
    /// bark that a slot of its own would be a draw call for a colour, and that submesh is merged into
    /// the tile's one tinted material anyway. No collider: this goes into a terrain tile mesh, and the
    /// tiles are not collidable. A pole standing in a road would be a fault, which is what
    /// <c>MinimumRoadClearance</c> is for.</para>
    /// </summary>
    public static class UtilityMeshes
    {
        /// <summary>Height of the pole above ground, metres.</summary>
        private const float PoleHeight = 8.4f;

        /// <summary>Half-width of the shaft at the foot and at the head, metres.</summary>
        private const float ButtHalf = 0.15f;
        private const float TopHalf = 0.11f;

        /// <summary>How far the pole is sunk, so uneven ground never shows its foot.</summary>
        private const float Bury = 0.7f;

        /// <summary>Height of the crossarm's underside above ground, metres.</summary>
        private const float ArmHeight = 7.5f;

        /// <summary>Half-length of the crossarm across the line, metres.</summary>
        private const float ArmHalf = 0.95f;

        private const float ArmThick = 0.07f;

        /// <summary>How far out along the arm the two wires hang, as a share of its half-length.</summary>
        private const float WireOut = 0.82f;

        /// <summary>Half-thickness of a wire, metres. Thin enough to read as a line at forty metres.</summary>
        private const float WireHalf = 0.035f;

        /// <summary>
        /// How far a wire sags below its ends, as a share of the span.
        ///
        /// <para>A catenary is not a parabola, and at these spans nobody can tell — three interior
        /// points on a quadratic is four quads and reads exactly like a wire. What matters is that it
        /// sags at all: a straight line between two poles reads as a rod, which reads as scaffolding.</para>
        /// </summary>
        private const float Sag = 0.055f;

        /// <summary>How many segments a span is drawn in. Three interior points.</summary>
        private const int SpanSteps = 4;

        /// <summary>
        /// The shortest a pole may stand from a carriageway's centreline, metres.
        ///
        /// <para>Well outside <c>TerrainShape.VergeWidth</c>'s shelf is not wanted here — a line that
        /// keeps a hundred metres away is a line nobody sees. This is the distance at which a nine-metre
        /// pole is beside the road rather than over it, and it is checked by the placer rather than
        /// assumed, because <c>MountainField.DistanceToRoad</c> is the only thing that knows.</para>
        /// </summary>
        public const float MinimumRoadClearance = 13f;

        /// <summary>One pole, standing on <paramref name="foot"/> and square to <paramref name="along"/>.</summary>
        /// <param name="along">The direction of the line, so the crossarm can lie across it.</param>
        public static void AddPole(VegetationMeshBuffer buffer, Vector3 foot, Vector3 along)
        {
            Vector3 across = Vector3.Cross(Vector3.up, along).normalized;

            if (across.sqrMagnitude < 0.5f)
            {
                across = Vector3.right;
            }

            Vector3 line = Vector3.Cross(across, Vector3.up).normalized;

            // Tapered, because a parallel-sided post reads as a bollard however tall it is. Two rings
            // rather than a box: the taper is the whole silhouette at this distance.
            AddTaper(buffer, foot - Vector3.up * Bury, across, line,
                ButtHalf, TopHalf, PoleHeight + Bury);

            Vector3 arm = foot + Vector3.up * ArmHeight;

            buffer.AddBox(PlantMeshes.BarkSubmesh, arm - across * ArmHalf - line * ArmThick,
                line, across, ArmThick, ArmHalf, ArmThick * 2f);

            // Two insulators, standing on the arm where the wires leave it. Without them the wire starts
            // in mid-air off the end of a stick.
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 at = arm + across * (ArmHalf * WireOut * side) + Vector3.up * (ArmThick * 2f);

                buffer.AddBox(PlantMeshes.BarkSubmesh, at, line, across, 0.07f, 0.07f, 0.16f);
            }
        }

        /// <summary>Where a wire leaves a pole standing on <paramref name="foot"/>.</summary>
        public static Vector3 WireAnchor(Vector3 foot, Vector3 along, float side)
        {
            Vector3 across = Vector3.Cross(Vector3.up, along).normalized;

            if (across.sqrMagnitude < 0.5f)
            {
                across = Vector3.right;
            }

            return foot + Vector3.up * (ArmHeight + ArmThick * 2f + 0.16f)
                   + across * (ArmHalf * WireOut * side);
        }

        /// <summary>
        /// Both wires of one span, sagging between two poles.
        ///
        /// <para>Drawn as a chain of thin boxes rather than as billboards, because a billboard needs a
        /// camera and this is baked into a terrain tile once. Four segments over a forty-metre span is
        /// ten metres a segment, which at the distance a wire is visible from is smooth.</para>
        /// </summary>
        public static void AddSpan(VegetationMeshBuffer buffer, Vector3 foot, Vector3 next, Vector3 along)
        {
            float span = Vector3.Distance(foot, next);

            if (span < 1f)
            {
                return;
            }

            float drop = span * Sag;

            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 from = WireAnchor(foot, along, side);
                Vector3 to = WireAnchor(next, along, side);

                Vector3 previous = from;

                for (int step = 1; step <= SpanSteps; step++)
                {
                    float t = step / (float)SpanSteps;
                    Vector3 point = Vector3.Lerp(from, to, t);

                    // 4t(1 − t) is one at the middle and nought at both ends, which is the sag.
                    point.y -= drop * 4f * t * (1f - t);

                    AddSegment(buffer, previous, point);
                    previous = point;
                }
            }
        }

        /// <summary>
        /// A four-sided prism between two points. The wire, one segment at a time.
        ///
        /// <para><b>The corner order is <c>VegetationMeshBuffer.AddBox</c>'s, deliberately.</b> The
        /// first version mixed the across and up offsets inside a single quad, which is not a face at
        /// all but a twisted saddle — and the build said so: 8538 faces wound backwards, on a counter
        /// that had read nought for the life of the project. The geometry still came out right, because
        /// <c>AddQuadFacing</c> corrects what it is given; the count is there precisely so a helper that
        /// has drifted is noticed rather than trusted. Walking the section's four corners in order and
        /// spanning each edge to the far end is the same shape a box already uses, and it needs no
        /// correction.</para>
        /// </summary>
        private static void AddSegment(VegetationMeshBuffer buffer, Vector3 from, Vector3 to)
        {
            Vector3 along = to - from;

            if (along.sqrMagnitude < 1e-4f)
            {
                return;
            }

            Vector3 across = Vector3.Cross(Vector3.up, along).normalized;
            Vector3 up = Vector3.Cross(along.normalized, across).normalized;

            Vector3 a = across * WireHalf;
            Vector3 b = up * WireHalf;

            // The section's four corners, walked round the axis, and the outward direction of the face
            // that spans each corner to the next.
            Vector3[] corner = { -a - b, a - b, a + b, -a + b };
            Vector3[] outward = { -up, across, up, -across };

            for (int i = 0; i < 4; i++)
            {
                Vector3 c0 = corner[i];
                Vector3 c1 = corner[(i + 1) & 3];

                buffer.AddQuadFacing(PlantMeshes.BarkSubmesh,
                    from + c0, from + c1, to + c1, to + c0, outward[i]);
            }
        }

        /// <summary>
        /// A four-sided shaft narrowing from <paramref name="butt"/> to <paramref name="top"/>.
        ///
        /// <para><b>The two lateral axes go in <c>line</c>-then-<c>across</c> order and that is not
        /// arbitrary.</b> <c>VegetationMeshBuffer.AddBox</c>'s winding assumes its two axes are handed
        /// so that forward × outward points up, which is what every caller elsewhere passes it — a road
        /// direction and a road right. Written the other way round the whole shaft comes out
        /// inside-out, and the only thing that says so is the flip counter: the build reported 3370
        /// faces corrected, which is 337 poles times the ten triangles of one taper, on a counter that
        /// had read nought for the life of the project. Nothing in any picture would have shown it,
        /// because <c>AddQuadFacing</c> turns them round.</para>
        /// </summary>
        private static void AddTaper(
            VegetationMeshBuffer buffer, Vector3 foot, Vector3 across, Vector3 line,
            float butt, float top, float height)
        {
            Vector3 rise = Vector3.up * height;

            Vector3 b0 = foot - line * butt - across * butt;
            Vector3 b1 = foot + line * butt - across * butt;
            Vector3 b2 = foot + line * butt + across * butt;
            Vector3 b3 = foot - line * butt + across * butt;

            Vector3 t0 = foot + rise - line * top - across * top;
            Vector3 t1 = foot + rise + line * top - across * top;
            Vector3 t2 = foot + rise + line * top + across * top;
            Vector3 t3 = foot + rise - line * top + across * top;

            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh, b0, b1, t1, t0, -across);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh, b2, b3, t3, t2, across);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh, b1, b2, t2, t1, line);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh, b3, b0, t0, t3, -line);
            buffer.AddQuadFacing(PlantMeshes.BarkSubmesh, t0, t1, t2, t3, Vector3.up);
        }
    }
}
