using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The cross-section of a town street: carriageway, kerb, footway.
    ///
    /// A town street wants two things the trunk road has no concept of — a kerb and a pavement — and
    /// wants none of what the trunk road carries. <see cref="RoadMeshBuilder"/> has a marking atlas whose
    /// v coordinate is arc length, row doubling where the marking variant changes, banking from measured
    /// curvature, and nine vertices every 2.5 m. None of that belongs on a street where the limit is
    /// 30 km/h, all of it would have to keep working through every change made here, and a dashed centre
    /// line down a lane would make it read as a main road.
    /// </summary>
    public struct TownStreetShape
    {
        [Tooltip("Half the driveable width, metres.")]
        public float HalfWidth;

        [Tooltip("How far the top of the kerb stands above the gutter, metres.")]
        public float KerbHeight;

        [Tooltip("Horizontal width of the kerb face itself. Not zero: a vertical face with no width "
               + "renders as a crack, and the strip needs an outward direction to face.")]
        public float KerbFace;

        [Tooltip("Width of the footway behind the kerb, metres.")]
        public float FootwayWidth;

        [Tooltip("Distance between cross-sections. 5 m, not the trunk road's 2.5 — a 30 km/h street with "
               + "no banking and no markings has nothing to facet.")]
        public float StepLength;

        [Tooltip("Lifts the surface clear of the terrain below it.")]
        public float SurfaceLift;

        [Tooltip("Camber: how much higher the centre of the carriageway sits than its gutters.")]
        public float Crown;

        /// <summary>Half the width of everything paved, kerbs and footways included.</summary>
        public float HalfOuter => HalfWidth + KerbFace + FootwayWidth;

        /// <summary>
        /// The cross-section for a kind of street.
        ///
        /// The steps between the kinds are what make a street network legible from inside a car: you can
        /// tell you have turned off the high street without being told.
        /// </summary>
        public static TownStreetShape For(TownStreetKind kind)
        {
            var shape = new TownStreetShape
            {
                HalfWidth = 3.4f,
                KerbHeight = 0.14f,
                KerbFace = 0.25f,
                FootwayWidth = 1.8f,
                StepLength = 5f,
                SurfaceLift = 0.08f,
                Crown = 0.06f,
            };

            switch (kind)
            {
                case TownStreetKind.HighStreet:
                    shape.HalfWidth = 4.6f;
                    shape.FootwayWidth = 3.2f;
                    shape.KerbHeight = 0.16f;
                    break;

                case TownStreetKind.Avenue:
                    shape.HalfWidth = 4.0f;
                    shape.FootwayWidth = 2.4f;
                    break;

                case TownStreetKind.Lane:
                    break;

                case TownStreetKind.Alley:
                    shape.HalfWidth = 2.6f;
                    shape.FootwayWidth = 0.7f;
                    shape.KerbHeight = 0.10f;
                    break;

                case TownStreetKind.SquareEdge:
                    shape.HalfWidth = 4.0f;
                    shape.FootwayWidth = 4.5f;
                    break;
            }

            return shape;
        }
    }

    /// <summary>
    /// Turns a street centreline into a ribbon with kerbs and footways either side.
    ///
    /// <para>Everything the town's streets emit goes into <b>one</b> buffer and ends up as one mesh under
    /// one chunk with a radius large enough never to unload — not one mesh per edge, and not one per
    /// terrain tile. Three kilometres of street is about thirteen thousand triangles and three draw
    /// calls, which is cheap enough that splitting it could only cost. It also makes every
    /// seam-at-a-tile-boundary question disappear, and gives the whole network a single
    /// <c>MeshCollider</c>. Worth revisiting past about eight kilometres of street.</para>
    /// </summary>
    public static class TownStreetBuilder
    {
        /// <summary>Asphalt.</summary>
        public const int SurfaceSubmesh = 0;

        /// <summary>The vertical faces of the kerbs.</summary>
        public const int KerbSubmesh = 1;

        /// <summary>The footways.</summary>
        public const int FootwaySubmesh = 2;

        public const int StreetSubmeshCount = 3;

        /// <summary>Which submesh each of the six strips across a section belongs to.</summary>
        private static readonly int[] StripSubmesh =
        {
            FootwaySubmesh, KerbSubmesh, SurfaceSubmesh, SurfaceSubmesh, KerbSubmesh, FootwaySubmesh,
        };

        /// <summary>Points across one section: two footways, two kerb faces, two halves of carriageway.</summary>
        private const int SectionPoints = 7;

        /// <summary>
        /// Adds one street's ribbon between two distances along its path.
        ///
        /// The trimmed range matters: a street stops short of its junctions so the pad can fill the
        /// middle. Callers pass the trim points from <see cref="StreetJunctionBuilder.ResolveTrims"/>.
        /// </summary>
        public static void AppendStreet(
            IRoadPath path,
            in TownStreetShape shape,
            float fromDistance,
            float toDistance,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            float from = Mathf.Clamp(fromDistance, 0f, path.Length);
            float to = Mathf.Clamp(toDistance, 0f, path.Length);
            if (to - from < 0.5f)
            {
                return;
            }

            int steps = Mathf.Max(1, Mathf.CeilToInt((to - from) / Mathf.Max(1f, shape.StepLength)));

            var previous = new Vector3[SectionPoints];
            var current = new Vector3[SectionPoints];

            CrossSection(path, shape, from, previous);

            for (int step = 1; step <= steps; step++)
            {
                float at = Mathf.Lerp(from, to, step / (float)steps);
                CrossSection(path, shape, at, current);

                for (int strip = 0; strip < StripSubmesh.Length; strip++)
                {
                    // Outward is up for every strip but the kerb faces, which look sideways — inwards,
                    // at the carriageway they edge, which is the only side of a kerb anyone sees. Taken
                    // as the direction from the kerb towards the crown so it comes out right on both
                    // sides of the street; the first version derived it from the strip's own two points
                    // and was therefore correct on the left kerb and backwards on the right.
                    Vector3 outward = Vector3.up;
                    if (StripSubmesh[strip] == KerbSubmesh)
                    {
                        outward = current[3] - current[strip];
                        outward.y = 0f;
                    }

                    // Along first, then across: Cross(along, across) points up, and the reverse order
                    // wound every face in the network backwards. The buffer corrects them, which is
                    // exactly why it also counts them.
                    into.AddQuadFacing(
                        StripSubmesh[strip],
                        previous[strip], current[strip], current[strip + 1], previous[strip + 1],
                        outward);
                }

                (previous, current) = (current, previous);
            }
        }

        /// <summary>
        /// The seven points across a street at one distance: outer footway edge, kerb top, gutter, crown,
        /// gutter, kerb top, outer footway edge.
        ///
        /// The crown is a point of its own rather than a lift applied to the whole carriageway, because a
        /// flat surface raised bodily is a plate — the camber has to be visible in the silhouette of the
        /// section, which means the middle has to be a vertex.
        /// </summary>
        private static void CrossSection(
            IRoadPath path, in TownStreetShape shape, float distance, Vector3[] into)
        {
            Vector3 centre = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            float half = shape.HalfWidth;
            float kerbTop = half + shape.KerbFace;
            float outer = shape.HalfOuter;
            float lift = shape.SurfaceLift;
            float top = lift + shape.KerbHeight;

            into[0] = Offset(centre, right, -outer, top);
            into[1] = Offset(centre, right, -kerbTop, top);
            into[2] = Offset(centre, right, -half, lift);
            into[3] = Offset(centre, right, 0f, lift + shape.Crown);
            into[4] = Offset(centre, right, half, lift);
            into[5] = Offset(centre, right, kerbTop, top);
            into[6] = Offset(centre, right, outer, top);
        }

        private static Vector3 Offset(Vector3 centre, Vector3 right, float across, float rise)
        {
            return centre + right * across + Vector3.up * rise;
        }

        /// <summary>
        /// A point on a street's cross-section: <paramref name="across"/> metres from the centreline,
        /// <paramref name="rise"/> metres above the surface's own datum.
        ///
        /// Public because the junction pads build their corners from it. Heights at a junction come from
        /// the ribbon's own section rather than from a second evaluation of the ground, which is what
        /// makes pad and ribbon flush to the millimetre instead of to within a tolerance.
        /// </summary>
        public static Vector3 PointAcross(
            IRoadPath path, in TownStreetShape shape, float distance, float across, float rise)
        {
            float at = Mathf.Clamp(distance, 0f, path.Length);
            return Offset(path.GetPositionAtDistance(at), path.GetRightAtDistance(at), across, rise);
        }

        /// <summary>
        /// The outer corner of a street's paved surface at a distance, left or right looking along it.
        ///
        /// Junction pads take their corners from here rather than re-deriving them, so a pad and the
        /// ribbon it meets are flush to the millimetre by construction. That is the general form of the
        /// bug the flushness check exists to catch.
        /// </summary>
        public static Vector3 OuterCorner(
            IRoadPath path, in TownStreetShape shape, float distance, bool leftSide)
        {
            Vector3 centre = path.GetPositionAtDistance(distance);
            Vector3 right = path.GetRightAtDistance(distance);

            float across = leftSide ? -shape.HalfOuter : shape.HalfOuter;
            return Offset(centre, right, across, shape.SurfaceLift + shape.KerbHeight);
        }
    }
}
