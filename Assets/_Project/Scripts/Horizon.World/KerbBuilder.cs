using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Red-and-white kerbs along the corners of a circuit.
    ///
    /// <para><b>This is the cheapest thing in the project that changes what a road <i>is</i>.</b> A
    /// thirteen-metre ribbon with no centre line is a wide road; the same ribbon with kerbs on the
    /// inside of every corner is a race track, and nobody has to be told which. Everything else the
    /// Weissjochring gets — the gantry, the pit buildings, the grid — is seen once a lap. The kerbs are
    /// in every frame.</para>
    ///
    /// <para><b>It costs one draw call and no material.</b> Two submeshes, tinted through
    /// <c>VegetationMeshBuffer.MergeTinted</c> onto the vertex-tinted road material that already exists
    /// for the trunk forks and the motorway merges. The rule that goes with that mechanism is the one
    /// already written down: never add a slot with a null tint.</para>
    ///
    /// <para><b>Which side is the inside is measured, not guessed.</b>
    /// <c>RoadPathExtensions.GetSignedCurvatureAtDistance</c> already answers it, and it has to be asked
    /// per sample rather than per corner: this circuit's rungs snake, so the inside changes hand twice
    /// in every four hundred metres and a kerb laid on one side throughout would spend half the lap on
    /// the outside of the corner it belongs to.</para>
    ///
    /// <para><b>No collider, deliberately.</b> A kerb stands on the shoulder, which the road mesh's own
    /// collider already carries, and the whole point of a kerb is that it can be driven over. Giving it
    /// one would also reproduce exactly the fault <c>GuardRailBuilder.BuildCollision</c> exists to
    /// avoid — a row of re-entrant corners for the car to catch on.</para>
    /// </summary>
    public static class KerbBuilder
    {
        /// <summary>Submesh carrying the red blocks.</summary>
        public const int RedSubmesh = 0;

        /// <summary>Submesh carrying the white ones.</summary>
        public const int WhiteSubmesh = 1;

        public const int KerbSubmeshCount = 2;

        /// <summary>
        /// Sampling step along the road, metres. Half a block, so a block boundary never falls in the
        /// middle of a quad and the two colours meet square across the kerb.
        /// </summary>
        private const float Step = 1.5f;

        /// <summary>Length of one colour block, metres.</summary>
        private const float BlockLength = 3f;

        /// <summary>Width of the kerb across the shoulder, metres.</summary>
        private const float KerbWidth = 1.2f;

        /// <summary>
        /// How far the kerb's outer lip stands above the asphalt edge, metres.
        ///
        /// <para>Eight centimetres, which is a real kerb and — not coincidentally —
        /// <c>RoadShape.SurfaceLift</c>. The inner lip sits flush with the asphalt, so the block is a
        /// ramp rather than a step: a kerb the car can hop is a corner that can be attacked, and a kerb
        /// it hits square is a wall painted like a kerb.</para>
        /// </summary>
        private const float KerbRise = 0.08f;

        /// <summary>
        /// Tightest radius that gets no kerb, metres — read the other way round: anything sharper than
        /// this is a corner.
        ///
        /// <para>450 rather than something rounder, because it has to sit above the circuit's own
        /// snake. Its rungs sweep at 180 to 420 m, so a threshold under 420 would kerb the tight rungs
        /// and leave the fast ones bare — which reads as kerbs that were forgotten rather than as a
        /// circuit whose fast sections happen to be straight.</para>
        /// </summary>
        private const float CornerRadius = 450f;

        /// <summary>
        /// Clearance either side of a fork, a forecourt or a portal, metres.
        ///
        /// <para>Sixty, matching <c>GuardRailBuilder.JunctionClearance</c>. A kerb across the mouth of
        /// the pit lane is a kerb across the road the pit lane exists to reach.</para>
        /// </summary>
        private const float FurnitureClearance = 60f;

        /// <summary>The two colours. Never null — see the class remarks.</summary>
        public static Color?[] KerbTints()
        {
            var tints = new Color?[KerbSubmeshCount];

            // Warm rather than pillar-box: this world is lit at golden hour and a pure red goes orange
            // in it anyway, so the tone is chosen against that light rather than against a paint chart.
            tints[RedSubmesh] = new Color(0.62f, 0.13f, 0.11f);

            // Not white. Nothing else in this world is 1,1,1 and a kerb that is would be the brightest
            // thing on the mountain at midday, snow included.
            tints[WhiteSubmesh] = new Color(0.88f, 0.88f, 0.86f);

            return tints;
        }

        /// <summary>
        /// Lays kerbs down both sides of every corner on <paramref name="path"/>.
        /// </summary>
        /// <param name="course">
        /// Optional. Read only to keep kerbs out of a fork's throat, off a forecourt's frontage and out
        /// of a tunnel — the same three things the guard rails stand off.
        /// </param>
        public static void Append(
            IRoadPath path,
            in RoadShape shape,
            RoadCourse course,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            float length = path.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / Step));

            // Walked as pairs so a quad can be laid between two consecutive sections only where both of
            // them want one — the same second-pass shape GuardRailBuilder uses, and for the same reason:
            // a run has to end cleanly rather than reach for a section that was never built.
            for (int i = 0; i < steps; i++)
            {
                float at = length * i / steps;
                float next = length * (i + 1) / steps;

                for (int side = 0; side < 2; side++)
                {
                    float sign = side == 0 ? -1f : 1f;

                    if (!Wanted(path, shape, course, at, sign)
                        || !Wanted(path, shape, course, next, sign))
                    {
                        continue;
                    }

                    int submesh = Mathf.FloorToInt(at / BlockLength) % 2 == 0
                        ? RedSubmesh
                        : WhiteSubmesh;

                    Edges(path, shape, at, sign, out Vector3 innerA, out Vector3 outerA);
                    Edges(path, shape, next, sign, out Vector3 innerB, out Vector3 outerB);

                    // Wound so the outward normal is up whichever hand the kerb is on.
                    if (sign < 0f)
                    {
                        into.AddQuadFacing(submesh, innerA, outerA, outerB, innerB, Vector3.up);
                    }
                    else
                    {
                        into.AddQuadFacing(submesh, outerA, innerA, innerB, outerB, Vector3.up);
                    }
                }
            }
        }

        /// <summary>
        /// Whether this side wants a kerb here: is it the inside or the exit of a real corner, and is
        /// the verge its own to stand on.
        /// </summary>
        private static bool Wanted(
            IRoadPath path, in RoadShape shape, RoadCourse course, float at, float sign)
        {
            if (course != null
                && (course.IsCoveredOrNear(at, FurnitureClearance)
                    || course.IsForecourt(at, FurnitureClearance)
                    || course.IsJunction(at, FurnitureClearance)))
            {
                return false;
            }

            float curvature = path.GetSignedCurvatureAtDistance(at, 8f);

            if (Mathf.Abs(curvature) < 1f / CornerRadius)
            {
                return false;
            }

            // A positive signed curvature turns towards +right, so the inside of the corner is the side
            // the sign points at. The outside gets a kerb too — the exit kerb is the one a car actually
            // uses — but only where the corner is tight enough to be run wide out of.
            float inside = Mathf.Sign(curvature);

            if (Mathf.Approximately(sign, inside))
            {
                return true;
            }

            return Mathf.Abs(curvature) > 1f / (CornerRadius * 0.5f);
        }

        /// <summary>The kerb's two edges across the shoulder, at a distance and on a side.</summary>
        private static void Edges(
            IRoadPath path, in RoadShape shape, float at, float sign,
            out Vector3 inner, out Vector3 outer)
        {
            Vector3 centre = path.GetPositionAtDistance(at);
            Vector3 right = path.GetBankedRightAtDistance(at, shape.MaxBankDegrees, shape.FullBankRadius);

            // The asphalt's own edge, crown included, and then a hair above it so the two never fight
            // for the same pixel.
            inner = centre + right * (shape.HalfWidth * sign);
            inner.y -= shape.Crown;
            inner.y += shape.SurfaceLift * 0.5f;

            outer = centre + right * ((shape.HalfWidth + KerbWidth) * sign);
            outer.y = inner.y + KerbRise;
        }
    }
}
