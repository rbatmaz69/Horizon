using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The acceleration lane that carries the link road onto the motorway.
    ///
    /// <para><b>What was there before: nothing.</b> The link's first 150 m are authored as a taper and
    /// are in fact exactly parallel to the motorway — a <c>Straight</c> on the same heading, at a
    /// constant 15.25 m centre to centre. The two paved surfaces never came within 2.5 m of each other,
    /// that gap was shoulder for its whole length, and the link's ribbon then stopped in a square end
    /// cap beside the carriageway. Coming down off the pass you arrived next to a motorway on a road
    /// that simply ended, with a strip of gravel between you and it. Nothing funnelled you anywhere.
    /// The previous fix in this area made the two ribbons meet in <i>height</i>; they still never met in
    /// plan.</para>
    ///
    /// <para><b>What this is.</b> A single wedge in the carriageway's own frame, starting at the link's
    /// end cap and running in the direction of travel: the link's mouth narrowing to one lane, a lane
    /// long enough to get up to speed on, and a gore closing to nothing against the carriageway's edge.
    /// Because it starts at the carriageway's paved edge and reaches outward far enough to cover the
    /// link's full width, it also swallows the gravel strip — the paving is continuous from the ramp to
    /// the motorway, which is the whole point.</para>
    ///
    /// <para><b>Built in the carriageway's frame rather than the link's</b>, so its inner edge is by
    /// construction exactly where the motorway's paving ends, at the motorway's own height, camber and
    /// bank. A wedge built off the link would have to agree with the carriageway across a seam that two
    /// separately-graded courses only approximately share, and disagreement there is a step at speed.
    /// The outer edge is a flat lateral extension: the camber is a parabola whose slope at the edge is
    /// small, and continuing it would put a crown in the middle of a merging lane.</para>
    ///
    /// <para><b>It deliberately overlays the carriageway's shoulder and the link's.</b> That strip is
    /// where the gravel was, and covering it is the fix. <see cref="Lift"/> is what keeps that from
    /// z-fighting, and it is small enough that a raycast wheel crossing onto it cannot feel the
    /// step.</para>
    ///
    /// <para>Vertex-coloured onto the shared tint material, the way the town's streets are, rather than
    /// wearing the motorway's surface atlas. That atlas is keyed on arc length in the motorway's own
    /// frame and carries four painted lanes; a wedge of changing width has no sensible u in it and would
    /// arrive covered in lane lines running diagonally across itself.</para>
    /// </summary>
    public static class MotorwayMergeBuilder
    {
        /// <summary>Asphalt.</summary>
        public const int SurfaceSubmesh = 0;

        /// <summary>The painted edge line down the outside of the lane.</summary>
        public const int MarkingSubmesh = 1;

        public const int MergeSubmeshCount = 2;

        /// <summary>
        /// How far the wedge sits above the carriageway's surface plane, metres.
        ///
        /// <para>It has to be above something: the wedge covers ground the carriageway's shoulder and
        /// the link's shoulder already pave, and three coplanar surfaces are three surfaces fighting for
        /// the depth buffer. Two centimetres wins that outright and is a twentieth of the suspension's
        /// own travel at rest, so it cannot be felt — the wheels are raycasts and land on whichever
        /// surface is highest, which is the one meant to be driven on.</para>
        /// </summary>
        public const float Lift = 0.02f;

        /// <summary>Width of the merging lane itself, metres. A motorway lane, near enough.</summary>
        private const float LaneWidth = 4f;

        /// <summary>Over which the link's full mouth narrows to that one lane.</summary>
        private const float MouthLength = 55f;

        /// <summary>Of lane at constant width — the part you actually accelerate on.</summary>
        private const float LaneLength = 175f;

        /// <summary>Over which the lane closes into the carriageway. The gore.</summary>
        private const float TaperLength = 85f;

        /// <summary>Total length of the merge, metres.</summary>
        public const float TotalLength = MouthLength + LaneLength + TaperLength;

        /// <summary>Rows every four metres. Finer than the motorway's own eight, because the closing
        /// taper is where a coarse step shows as a staircase along the painted edge.</summary>
        private const float StepLength = 4f;

        /// <summary>
        /// Width of the painted edge line, metres. Wider than a lane divider because this one is the
        /// gore's edge — the line that tells you the lane you are in is about to run out.
        /// </summary>
        private const float LineWidth = 0.3f;

        /// <summary>
        /// The colour each submesh is tinted with.
        ///
        /// <para><b>The motorway's own numbers, not the town's.</b> These are <c>AsphaltBase</c> and
        /// <c>PaintBase</c> from <c>RoadTextureBuilder</c>, which is what bakes the surface the wedge
        /// runs alongside. Restated here rather than referenced because that builder is editor-only and
        /// this has to compile into a player — the same reason <c>VehicleBodySet</c> restates a submesh
        /// index. Taking the town street's 0.27 instead, which was the first thing tried, laid a
        /// visibly paler strip down the side of the motorway: near enough to asphalt to be asphalt, far
        /// enough off to read as a different road.</para>
        ///
        /// <para>The tint material also has to carry the road's smoothness rather than the terrain's.
        /// <c>M_TerrainTint</c> is matte at 0.08 because it is grass and rock; asphalt is 0.34, and the
        /// gap between those is most of why the first attempt looked like a patch.</para>
        /// </summary>
        public static Color?[] SurfaceTints()
        {
            var tints = new Color?[MergeSubmeshCount];

            tints[SurfaceSubmesh] = new Color(0.200f, 0.195f, 0.205f);
            tints[MarkingSubmesh] = new Color(0.86f, 0.86f, 0.83f);

            return tints;
        }

        /// <summary>
        /// How wide the wedge is at <paramref name="along"/> metres past the link's end cap, measured
        /// outward from the carriageway's paved edge.
        /// </summary>
        public static float WidthAt(float along, float mouthWidth)
        {
            if (along <= 0f)
            {
                return mouthWidth;
            }

            if (along < MouthLength)
            {
                return Mathf.Lerp(mouthWidth, LaneWidth, along / MouthLength);
            }

            if (along < MouthLength + LaneLength)
            {
                return LaneWidth;
            }

            float into = along - MouthLength - LaneLength;
            return into >= TaperLength ? 0f : Mathf.Lerp(LaneWidth, 0f, into / TaperLength);
        }

        /// <summary>
        /// Appends the wedge.
        /// </summary>
        /// <param name="carriageway">The carriageway being merged onto, as its own offset path.</param>
        /// <param name="atDistance">Distance along it where the link's end cap sits.</param>
        /// <param name="mouthWidth">
        /// How far out the wedge reaches at the cap. Wide enough to cover the link's paving and the
        /// gravel between the two, or the seam it exists to remove is still there at the one point the
        /// player crosses it.
        /// </param>
        /// <param name="side">
        /// Which side of the carriageway the link lies on: −1 for the left of the motorway's forward
        /// direction, +1 for the right. <b>Measured by the caller, never assumed</b> — a wedge on the
        /// wrong side is 300 m of asphalt laid across the opposite carriageway.
        /// </param>
        /// <param name="travelSign">
        /// Which way distance runs for a driver arriving off the link: −1 if they merge towards
        /// decreasing distance along the carriageway, +1 towards increasing.
        /// </param>
        public static void Append(
            IRoadPath carriageway,
            in RoadShape shape,
            float atDistance,
            float mouthWidth,
            float side,
            float travelSign,
            VegetationMeshBuffer into)
        {
            if (carriageway == null || into == null)
            {
                return;
            }

            int steps = Mathf.Max(2, Mathf.CeilToInt(TotalLength / StepLength));

            Vector3 previousInner = Vector3.zero;
            Vector3 previousOuter = Vector3.zero;
            Vector3 previousUp = Vector3.up;
            float previousWidth = 0f;

            for (int i = 0; i <= steps; i++)
            {
                float along = TotalLength * i / steps;
                float width = WidthAt(along, mouthWidth);

                Section(carriageway, shape, atDistance + travelSign * along, side, width,
                    out Vector3 inner, out Vector3 outer, out Vector3 up);

                if (i > 0)
                {
                    // Outward is the surface normal, so the winding is decided by the geometry rather
                    // than by which way round the caller happened to hand in the two edges.
                    into.AddQuadFacing(SurfaceSubmesh, previousInner, previousOuter, outer, inner, up);

                    // The painted edge, drawn only while there is a lane left to edge. Past that the
                    // wedge is a few centimetres wide and a line down it would be the whole surface.
                    if (previousWidth > LineWidth * 2f && width > LineWidth * 2f)
                    {
                        Vector3 previousIn = Mathf.Approximately(previousWidth, 0f)
                            ? previousOuter
                            : Vector3.Lerp(previousOuter, previousInner, LineWidth / previousWidth);

                        Vector3 currentIn = Vector3.Lerp(outer, inner, LineWidth / width);

                        into.AddQuadFacing(
                            MarkingSubmesh,
                            previousOuter + previousUp * Lift * 0.5f,
                            previousIn + previousUp * Lift * 0.5f,
                            currentIn + up * Lift * 0.5f,
                            outer + up * Lift * 0.5f,
                            up);
                    }
                }

                previousInner = inner;
                previousOuter = outer;
                previousUp = up;
                previousWidth = width;
            }
        }

        /// <summary>One cross-section: the carriageway's paved edge, and a point that far outboard.</summary>
        private static void Section(
            IRoadPath carriageway,
            in RoadShape shape,
            float distance,
            float side,
            float width,
            out Vector3 inner,
            out Vector3 outer,
            out Vector3 up)
        {
            float at = Mathf.Clamp(distance, 0f, carriageway.Length);

            Vector3 center = carriageway.GetPositionAtDistance(at);

            // The banked right, matching what RoadMeshBuilder lays the carriageway out along. Taking the
            // level one instead would leave the wedge flat while the road it joins is cambered, and the
            // seam would open by HalfWidth × sin(bank) through every curve.
            Vector3 right = carriageway.GetBankedRightAtDistance(
                at, shape.MaxBankDegrees, shape.FullBankRadius);

            up = Vector3.Cross(carriageway.GetDirectionAtDistance(at), right).normalized;
            if (up.y < 0f)
            {
                up = -up;
            }

            Vector3 surface = center + up * (shape.SurfaceLift + Lift);

            inner = surface + right * (side * shape.HalfWidth);
            outer = surface + right * (side * (shape.HalfWidth + width));
        }
    }
}
