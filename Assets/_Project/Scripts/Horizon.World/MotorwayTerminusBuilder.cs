using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Where the motorway stops being a motorway: the two carriageways come together and one ordinary
    /// road carries on.
    ///
    /// <para><b>What was there instead.</b> Nothing at all. <c>AutobahnCourse</c> hands its two ends to
    /// two other roads — Hochstadt's boulevard at the east and the coast road at the west — and both of
    /// them begin on the <i>median line</i>, because that is the axis the whole motorway is measured
    /// against. The carriageways are <see cref="OffsetRoadPath"/>s at ±<c>CarriagewayOffset</c>, so each
    /// one ended ten and a half metres to the side of the road it was handing over to, with six metres
    /// of unpaved median between them and, down the middle of that, the median barrier — which ran the
    /// full length of the course and was solid. The last post stood on the city gate. There was no way
    /// out of Hochstadt and no way onto the coast road, and nothing in the build said so: every check
    /// this project has walks <i>along</i> a road or measures the ground under it, and this is a fault
    /// that exists only in the gap between two of them.</para>
    ///
    /// <para><b>The carriageways are trimmed and this replaces them.</b> Not laid over them — the two
    /// ribbons and this one would then disagree about the camber by a whole <c>Crown</c> at each
    /// carriageway's centreline, which is 12 cm, three times what a seam is allowed. The ribbons stop
    /// <see cref="TerminusLength"/> short (<c>RoadMeshBuilder.BuildRoad</c>'s trim) and the cross-section
    /// here <i>is</i> theirs at that end and the onward road's at the other, blended between: two crowns
    /// becoming one, a paved half-width of <c>offset + HalfWidth</c> becoming the onward road's, and the
    /// shoulder and its drop going with them. So the seam at either end is exact rather than tolerable.
    /// </para>
    ///
    /// <para>Built in the <b>median's</b> frame, because that is the one line all three roads agree
    /// about: both carriageways are offsets of it and the onward road starts on it. Working in either
    /// carriageway's frame would need the answer this is trying to produce.</para>
    /// </summary>
    public static class MotorwayTerminusBuilder
    {
        /// <summary>Asphalt. Tinted, so it merges into the one road-tint draw call.</summary>
        public const int SurfaceSubmesh = 0;

        /// <summary>
        /// The gravel either side.
        ///
        /// <para>It carries <b>no tint</b>, and that is an instruction rather than an oversight:
        /// <c>VegetationMeshBuffer.MergeTinted</c> folds every tinted slot into one draw call, and a
        /// gravel shoulder wants the shoulder material — the same rule the paddock's start/finish board
        /// and its road paint are on the other side of.</para>
        /// </summary>
        public const int ShoulderSubmesh = 1;

        public const int TerminusSubmeshCount = 2;

        /// <summary>
        /// Over how many metres the dual carriageway becomes one road.
        ///
        /// <para>Long enough that a driver at motorway speed is guided rather than pinched — about seven
        /// seconds at 100 km/h — and short enough to sit inside the straight, level run both ends of this
        /// motorway already have. <c>GuardRailBuilder.MedianEndClearance</c> is deliberately larger, so
        /// the barrier is gone before the taper starts rather than ending inside it.</para>
        /// </summary>
        public const float TerminusLength = 200f;

        /// <summary>Rows every four metres, matching <c>MotorwayMergeBuilder</c>'s.</summary>
        private const float StepLength = 4f;

        /// <summary>
        /// Spans across one row. Even, so a vertex lands on the median line and the crown is sampled
        /// symmetrically — the profile is two parabolas at one end and one at the other, and a row that
        /// straddled either apex instead of standing on it would chord the top off it.
        /// </summary>
        private const int AcrossSteps = 16;

        /// <summary>
        /// The colours. See <see cref="ShoulderSubmesh"/> for why exactly one of them is null.
        /// </summary>
        public static Color?[] SurfaceTints()
        {
            var tints = new Color?[TerminusSubmeshCount];

            tints[SurfaceSubmesh] = new Color(0.200f, 0.195f, 0.205f);

            return tints;
        }

        /// <summary>
        /// Paved half-width at <paramref name="along"/> metres back from the end of the motorway.
        /// </summary>
        public static float HalfWidthAt(float along, float wide, float narrow)
        {
            return Mathf.Lerp(narrow, wide, Fraction(along));
        }

        /// <summary>How far through the change from one road to two, 0 at the end and 1 at the start.</summary>
        private static float Fraction(float along)
        {
            float t = Mathf.Clamp01(TerminusLength <= 0f ? 1f : along / TerminusLength);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Appends the terminus.
        /// </summary>
        /// <param name="median">The line the carriageways are offset from.</param>
        /// <param name="motorwayShape">The carriageways' shape.</param>
        /// <param name="carriagewayOffset">How far each carriageway's centreline sits off the median.</param>
        /// <param name="onwardShape">The shape of the road that carries on past the end.</param>
        /// <param name="atDistance">
        /// Distance along the median of the end itself — 0 at the western tip, <c>median.Length</c> at
        /// the eastern. <b>Not assumed:</b> this motorway hands over at both of its ends and they run
        /// opposite ways.
        /// </param>
        /// <param name="travelSign">
        /// Which way distance along the median runs when walking back <i>into</i> the motorway from that
        /// end: +1 from the western tip, −1 from the eastern.
        /// </param>
        public static void Append(
            IRoadPath median,
            in RoadShape motorwayShape,
            float carriagewayOffset,
            in RoadShape onwardShape,
            float atDistance,
            float travelSign,
            VegetationMeshBuffer into)
        {
            if (median == null || into == null)
            {
                return;
            }

            int steps = Mathf.Max(2, Mathf.CeilToInt(TerminusLength / StepLength));

            float wide = carriagewayOffset + motorwayShape.HalfWidth;
            float narrow = onwardShape.HalfWidth;

            var previousSurface = new Vector3[AcrossSteps + 1];
            var previousVerge = new Vector3[2];
            bool have = false;

            var surface = new Vector3[AcrossSteps + 1];
            var verge = new Vector3[2];

            for (int i = 0; i <= steps; i++)
            {
                float along = TerminusLength * i / steps;
                float at = Mathf.Clamp(atDistance + travelSign * along, 0f, median.Length);

                Vector3 centre = median.GetPositionAtDistance(at);

                // The banked right, matching what RoadMeshBuilder laid the two carriageways out along.
                // Both are offsets of this line, so they share its bank.
                Vector3 right = median.GetBankedRightAtDistance(
                    at, motorwayShape.MaxBankDegrees, motorwayShape.FullBankRadius);

                Vector3 up = Vector3.Cross(median.GetDirectionAtDistance(at), right).normalized;
                if (up.y < 0f)
                {
                    up = -up;
                }

                float fraction = Fraction(along);
                float half = Mathf.Lerp(narrow, wide, fraction);
                float shoulder = Mathf.Lerp(onwardShape.ShoulderWidth, motorwayShape.ShoulderWidth, fraction);
                float drop = Mathf.Lerp(onwardShape.ShoulderDrop, motorwayShape.ShoulderDrop, fraction);

                // The lift blends too. It is the same on every RoadShape in this world today and
                // deliberately not on a TownStreetShape, which sits lower so its streets are not
                // plateaux — and the east terminus hands over to a boulevard.
                Vector3 middle = centre + up * Mathf.Lerp(
                    onwardShape.SurfaceLift, motorwayShape.SurfaceLift, fraction);

                for (int k = 0; k <= AcrossSteps; k++)
                {
                    float u = k * 2f / AcrossSteps - 1f;
                    float across = u * half;

                    surface[k] = middle + right * across + up * CrownAt(
                        across, half, carriagewayOffset, motorwayShape, onwardShape, fraction);
                }

                verge[0] = surface[0] - right * shoulder - up * drop;
                verge[1] = surface[AcrossSteps] + right * shoulder - up * drop;

                if (have)
                {
                    for (int k = 0; k < AcrossSteps; k++)
                    {
                        into.AddQuadFacing(SurfaceSubmesh,
                            previousSurface[k], previousSurface[k + 1], surface[k + 1], surface[k], up);
                    }

                    into.AddQuadFacing(ShoulderSubmesh,
                        previousVerge[0], previousSurface[0], surface[0], verge[0], up);
                    into.AddQuadFacing(ShoulderSubmesh,
                        previousSurface[AcrossSteps], previousVerge[1], verge[1], surface[AcrossSteps], up);
                }

                System.Array.Copy(surface, previousSurface, surface.Length);
                System.Array.Copy(verge, previousVerge, verge.Length);
                have = true;
            }
        }

        /// <summary>
        /// The camber this far across, blended from the onward road's single crown to the two the
        /// carriageways carry.
        ///
        /// <para>Both profiles are the parabola <c>RoadMeshBuilder.AppendRing</c> lays: the crown at the
        /// centreline, zero at the paved edges. Taking each of them at its own end and interpolating is
        /// what makes both seams exact, and it is the only reason this is not one flat slab.</para>
        /// </summary>
        private static float CrownAt(
            float across,
            float half,
            float carriagewayOffset,
            in RoadShape motorwayShape,
            in RoadShape onwardShape,
            float fraction)
        {
            float u = half > 0.01f ? across / half : 0f;
            float single = onwardShape.Crown * (1f - u * u);

            float local = motorwayShape.HalfWidth > 0.01f
                ? (Mathf.Abs(across) - carriagewayOffset) / motorwayShape.HalfWidth
                : 0f;

            local = Mathf.Clamp(local, -1f, 1f);

            float pair = motorwayShape.Crown * (1f - local * local);

            return Mathf.Lerp(single, pair, fraction);
        }
    }
}
