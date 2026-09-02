using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The bell-mouth where a branch road leaves a trunk road out in the country.
    ///
    /// <para><b>What is missing without it.</b> Two roads that meet are two ribbons that cross.
    /// <c>RoadMeshBuilder</c> lays each one out along its own course, so either side of the branch's
    /// edge there is a triangle of shoulder gravel with nothing paved across it, and the branch's last
    /// cross-section — square to the branch, so most of it is across the trunk — ends in a cap on the
    /// driving line. The junction reads as one road dropped on top of another, which is what it is.</para>
    ///
    /// <para><b>The branch's ribbon stops before the trunk's carriageway and this pays it back.</b> See
    /// <see cref="RibbonTrim"/> for where it stops and why the earlier answer — let both ribbons run to
    /// the junction point and lay a throat over the overlap — was wrong: it put paving across the trunk,
    /// which on a circuit is across the racing line, and a flat plane over a cambered carriageway
    /// besides. The trunk keeps its own surface, its markings and its kerbs, and the throat opens off
    /// its paved edge.</para>
    ///
    /// <para><b>Why it is a throat in the branch's frame and not a pad in the trunk's.</b> The thing
    /// that must not have a step in it is the line a driver takes, and the only line through a fork
    /// that is not already paved is the one turning off. Laid along the branch, the throat's two edges
    /// are by construction parallel to the branch's own paving and meet it flush at the far end; laid
    /// across the trunk it would meet the branch at whatever angle the fork happens to have, and the
    /// seam a car crosses would be the one nobody measured. It is the same argument
    /// <see cref="MotorwayMergeBuilder"/> makes for building its wedge in the carriageway's frame, with
    /// the roles the other way round because there the driver is joining and here they are leaving.</para>
    ///
    /// <para><b>The surface is the trunk's at the mouth and the branch's beyond it.</b> A throat that
    /// simply followed the branch would carry the branch's camber and grade over ground the trunk has
    /// already decided, and the seam across the trunk's own carriageway would open by half a road width
    /// times the difference. So each station is built twice — once on the branch and once projected
    /// onto the trunk's local surface plane — and blended over <see cref="BlendLength"/>. That is also
    /// what makes <c>RoadCourseBuilder.AddJunction</c>'s rule load-bearing rather than advisory: the
    /// projection is onto a <i>plane</i>, so a fork marked inside a bend or on a camber would be flush
    /// at the centre and wrong at the edges.</para>
    ///
    /// <para>Laid on top of the branch at <see cref="MotorwayMergeBuilder.Lift"/> rather than let
    /// through beside it, for the reason recorded there: two centimetres wins the depth test outright
    /// while being a twentieth of the suspension's travel at rest, so a raycast wheel crossing onto it
    /// cannot feel the step. Against the trunk it does not need to win anything, because it stops at
    /// the paved edge — where <c>RoadMeshBuilder.AppendRing</c> puts the camber at exactly zero, so the
    /// two surfaces are flush there and the seam is the one the road already had.</para>
    ///
    /// <para><b>Unmarked, deliberately.</b> The trunk's markings live in a baked atlas whose v is arc
    /// length in its own frame, so two paths share no dash phase and there is no unpainted column to
    /// fall back on. <c>StreetJunctionBuilder.AppendTrunkMouth</c> reached the same conclusion for the
    /// town's mouths: painting a junction is a texture problem, not a geometry one.</para>
    /// </summary>
    public static class TrunkForkBuilder
    {
        /// <summary>Asphalt. The only submesh — see the note about markings on the class.</summary>
        public const int SurfaceSubmesh = 0;

        public const int ForkSubmeshCount = 1;

        /// <summary>
        /// How far up the branch the mouth flares, metres.
        ///
        /// <para>Long enough that the widening is a bell rather than a chamfer, and short enough that
        /// it is over before the branch's first corner does anything. It also has to comfortably clear
        /// the crossing itself: a branch leaving at 32° crosses a 17 m trunk over some 32 m of its own
        /// length, and a throat that stopped inside that would leave the far corner of the overlap
        /// uncovered — which is the one place the z-fighting this exists to hide would show.</para>
        ///
        /// <para><b>88 rather than 70, because <see cref="RibbonTrim"/> is measured against it and every
        /// term of that trim is a width.</b> The Bahçe Ring's pit road leaves a 16.2 m circuit at 18°,
        /// where the trim is already most of this length; the roads growing a quarter for the cars takes
        /// it past 70. Past that the trim is longer than the throat covering it, which is a branch
        /// stopping short of the road it joins.</para>
        /// </summary>
        private const float ThroatLength = 88f;

        /// <summary>
        /// Over how much of that the surface leaves the trunk's plane for the branch's own, metres.
        ///
        /// <para>One verge width, which is the distance <c>MountainField</c> already treats as "still
        /// this road's ground". Shorter and the blend is a ridge across the mouth; longer and the throat
        /// is still arguing with the trunk after it has stopped touching it.</para>
        /// </summary>
        private const float BlendLength = 24f;

        /// <summary>
        /// How far past the wider of the two roads' paved edges the mouth reaches at the trunk, metres.
        ///
        /// <para>A quarter of a metre, which is the town's <c>RibbonOverlap</c> and is here for the same
        /// reason: two surfaces that end on exactly the same line leave a seam a raycast wheel can drop
        /// through, and the fix is for one of them to reach past the other rather than to meet it.</para>
        /// </summary>
        private const float MouthOverlap = 0.25f;

        /// <summary>Rows every three metres. Finer than the road's own, because the flare is short.</summary>
        private const float StepLength = 3f;

        /// <summary>
        /// How far out from the trunk's paved edge the corner fillets reach before they close, metres.
        ///
        /// <para><b>Without these there is no junction, only two roads that pass close.</b> The throat
        /// above paves the branch's own width and nothing else, so between the branch's edge and the
        /// trunk's there is a wedge of verge that nothing turns the corner across — and the first set of
        /// preview shots came back showing exactly that: arriving on the branch, the road it joins is a
        /// separate ribbon thirty metres to one side with grass and a shoulder drop in between. The
        /// build had nothing to say about it. It is the fault <c>StreetJunctionBuilder.AppendTrunkMouth</c>
        /// records paying for in the town's mouths, one road class later.</para>
        ///
        /// <para>Eighteen metres, which on a 32° fork carries the corners some forty to sixty metres up
        /// the branch — generous enough to read as a mouth from a moving car and short enough that it is
        /// not a lay-by. It closes by <i>tapering</i> over <see cref="FilletTaper"/> rather than by
        /// stopping, so the outer edge is a curve and not a line ruled across the grass.</para>
        ///
        /// <para><b>The first version tapered over the whole reach and built nothing anyone could
        /// see.</b> The fill fraction was driven by the same gap that sets the fillet's width, so the
        /// two cancelled: the widest the paving ever got was about three and a half metres, on a wedge
        /// fourteen wide. It built, it validated, the corridor was clear, and the picture from above was
        /// pixel-for-pixel the one that had no fillets in it at all. A quantity that both opens a shape
        /// and closes it is a quantity that produces nothing.</para>
        /// </summary>
        private const float FilletReach = 18f;

        /// <summary>Over how much of the far end of that reach the paving closes to nothing, metres.</summary>
        private const float FilletTaper = 6f;

        /// <summary>How far up the branch the fillets are looked for. Past this the gap is always open.</summary>
        private const float FilletSearch = 90f;

        /// <summary>
        /// The colour the throat is tinted. <c>RoadTextureBuilder</c>'s <c>AsphaltBase</c>, restated
        /// here for the reason <see cref="MotorwayMergeBuilder.SurfaceTints"/> restates it: that builder
        /// is editor-only and this has to compile into a player.
        /// </summary>
        public static Color?[] SurfaceTints()
        {
            var tints = new Color?[ForkSubmeshCount];
            tints[SurfaceSubmesh] = new Color(0.200f, 0.195f, 0.205f);
            return tints;
        }

        /// <summary>
        /// How wide the throat is at <paramref name="along"/> metres up the branch from the mouth,
        /// measured either side of the branch's centreline.
        ///
        /// <para>A smoothstep rather than a straight taper, so the flare leaves the branch's own width
        /// tangentially. A linear one puts a visible corner in the kerb line at the point the widening
        /// starts, which is exactly where a driver is looking on the way in.</para>
        /// </summary>
        /// <summary>
        /// How far short of the junction the branch's <b>own ribbon</b> has to stop, metres along the
        /// branch. Fed to <c>RoadMeshBuilder.BuildRoad</c>'s trim.
        ///
        /// <para>A cross-section is square to the branch, so a ribbon walked all the way to the junction
        /// point lays its last one across the road it is joining: half a carriageway of asphalt, a gravel
        /// shoulder and the drop under it, ending in a square cap on somebody's driving line. The row has
        /// cleared the trunk's paved edge once <c>along * sin(fork)</c> exceeds
        /// <c>trunkHalfWidth + branchOuterHalfWidth * cos(fork)</c>, and that is this.</para>
        ///
        /// <para><b>Measured from the two paths rather than told.</b> The fork angle is authored on the
        /// branch's course as a <c>ForkDeflection</c>, but what the ribbon is actually built along is a
        /// <c>RoadPath</c> through control points a Dubins solve landed on, and the two agree to within
        /// whatever the last arc did. <c>BuildTrunkFork</c> already measures the mouth's position for the
        /// same reason.</para>
        /// </summary>
        public static float RibbonTrim(
            IRoadPath trunk,
            in RoadShape trunkShape,
            float atDistance,
            IRoadPath branch,
            in RoadShape branchShape,
            float branchAt,
            float branchSign)
        {
            if (trunk == null || branch == null)
            {
                return 0f;
            }

            Vector3 alongTrunk = trunk.GetDirectionAtDistance(Mathf.Clamp(atDistance, 0f, trunk.Length));
            Vector3 alongBranch = branch.GetDirectionAtDistance(Mathf.Clamp(branchAt, 0f, branch.Length))
                                  * branchSign;

            alongTrunk.y = 0f;
            alongBranch.y = 0f;

            if (alongTrunk.sqrMagnitude < 0.0001f || alongBranch.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            alongTrunk.Normalize();
            alongBranch.Normalize();

            float cos = Mathf.Abs(Vector3.Dot(alongTrunk, alongBranch));
            float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));

            // A branch that leaves at nothing at all never clears the trunk, and the expression below
            // would answer with a kilometre. Sixteen degrees is the floor BahceRingCourse.ForkDeflection
            // records for a different reason; below it there is no fork to trim for.
            if (sin < 0.27f)
            {
                Debug.LogWarning(
                    $"[Horizon] A fork leaves its trunk road at {Mathf.Asin(sin) * Mathf.Rad2Deg:0.0}°. "
                    + "Below about sixteen degrees a branch's ribbon cannot be trimmed clear of the "
                    + "carriageway inside the throat, and it will be laid over it instead.");
                return 0f;
            }

            float trim = (trunkShape.HalfWidth + branchShape.OuterHalfWidth * cos) / sin + MouthOverlap;

            if (trim > ThroatLength)
            {
                Debug.LogWarning(
                    $"[Horizon] A branch's ribbon has to stop {trim:0} m short of its junction, and the "
                    + $"throat that pays it back is only {ThroatLength:0} m long. There will be a hole in "
                    + "the road between them. Widen the fork angle or lengthen ThroatLength.");
            }

            return trim;
        }

        /// <summary>
        /// How wide the mouth opens at the trunk, either side of the branch's centreline.
        ///
        /// <para>Exposed because the build reports it, and reported it wrongly for as long as it has
        /// existed: <c>BuildTrunkFork</c> printed <c>branchShape.OuterHalfWidth</c> and called it "at the
        /// mouth", which was the same number by coincidence while both forks in the world joined two
        /// roads of one class. It is a second copy of a formula, and the moment the first copy learned
        /// about the trunk the two disagreed — silently, in the one line anybody would have read to
        /// check.</para>
        /// </summary>
        public static float MouthHalfWidth(in RoadShape branchShape, in RoadShape trunkShape)
        {
            return Mathf.Max(branchShape.OuterHalfWidth, trunkShape.OuterHalfWidth) + MouthOverlap;
        }

        public static float HalfWidthAt(float along, float branchHalfWidth, float mouthHalfWidth)
        {
            if (along <= 0f)
            {
                return mouthHalfWidth;
            }

            if (along >= ThroatLength)
            {
                return branchHalfWidth;
            }

            float t = along / ThroatLength;
            return Mathf.Lerp(mouthHalfWidth, branchHalfWidth, t * t * (3f - 2f * t));
        }

        /// <summary>
        /// Appends the mouth.
        /// </summary>
        /// <param name="trunk">The road being left. Its surface decides the throat's at the mouth.</param>
        /// <param name="trunkShape">Its cross-section.</param>
        /// <param name="atDistance">
        /// Where the fork falls along the trunk. Take it from the course's own
        /// <c>RoadFeatureKind.Junction</c> mark rather than counting: the mark is set from the walk, and
        /// a fork is the one feature two courses have to agree about.
        /// </param>
        /// <param name="branch">The road leaving.</param>
        /// <param name="branchShape">Its cross-section.</param>
        /// <param name="branchAt">Where the mouth falls along the branch — one of its two ends.</param>
        /// <param name="branchSign">
        /// Which way the throat runs from there: +1 towards increasing distance along the branch, −1
        /// towards decreasing. <b>Measured by the caller, never assumed</b> — a branch grafted onto the
        /// fork and a branch solved into it run opposite ways, and the wrong sign builds the throat off
        /// the end of the road.
        /// </param>
        /// <param name="into">Buffer to append to.</param>
        public static void Append(
            IRoadPath trunk,
            in RoadShape trunkShape,
            float atDistance,
            IRoadPath branch,
            in RoadShape branchShape,
            float branchAt,
            float branchSign,
            VegetationMeshBuffer into)
        {
            if (trunk == null || branch == null || into == null)
            {
                return;
            }

            // The trunk's local surface plane at the fork: a point on it and its normal. AddJunction
            // requires this stretch to be straight and level precisely so one plane is enough.
            float trunkAt = Mathf.Clamp(atDistance, 0f, trunk.Length);
            Vector3 trunkCentre = trunk.GetPositionAtDistance(trunkAt);
            Vector3 trunkRight = trunk.GetBankedRightAtDistance(
                trunkAt, trunkShape.MaxBankDegrees, trunkShape.FullBankRadius);
            Vector3 trunkUp = Vector3.Cross(trunk.GetDirectionAtDistance(trunkAt), trunkRight).normalized;

            if (trunkUp.y < 0f)
            {
                trunkUp = -trunkUp;
            }

            Vector3 trunkSurface = trunkCentre + trunkUp * (trunkShape.SurfaceLift + MotorwayMergeBuilder.Lift);

            // Vertical, not along the normal: the throat is being asked what height to sit at above a
            // given point in plan, and a road that is banked has a normal that answers a different
            // question. Guarded because a plane on its edge has no answer at all.
            float PlaneHeight(Vector3 planar)
            {
                if (Mathf.Abs(trunkUp.y) < 0.01f)
                {
                    return trunkSurface.y;
                }

                float dx = planar.x - trunkSurface.x;
                float dz = planar.z - trunkSurface.z;
                return trunkSurface.y - (dx * trunkUp.x + dz * trunkUp.z) / trunkUp.y;
            }

            // The mouth has to open onto the road it is joining, and this was sized from the branch
            // alone.
            //
            // <b>On a fork between two country roads that is the same number written twice, so nothing
            // showed.</b> Both of this world's first two forks join a Default carriageway to a Default
            // carriageway; the throat flared from 5.3 m to 6.8 and looked like a mouth because 6.8 is
            // most of the way across what it was opening onto. A circuit is 6.5 m of asphalt inside a
            // 9.5 m half-width, and the same expression still returned 6.8 — a bell that is *narrower*
            // at its widest than the road it opens onto. The junction pinched shut exactly where it
            // should have been at its most open, and from the car it read as a lane running into the
            // side of the track rather than as a way on to it. Which is what it was reported as.
            //
            // AppendFillets has always taken its reach from the trunk's own edge. This is the same
            // fact, and it belonged here too.
            float mouthHalfWidth = MouthHalfWidth(branchShape, trunkShape);
            int steps = Mathf.Max(2, Mathf.CeilToInt(ThroatLength / StepLength));

            // Which hand the branch leaves on, measured over the whole throat rather than read off a
            // tangent: at the mouth the branch's centreline is on the trunk's own, so the sign there is
            // whatever the solve's last millimetre happened to be. Over seventy metres at the shallowest
            // fork this world has it is twenty-two metres of separation.
            Vector3 mouthCentre = branch.GetPositionAtDistance(Mathf.Clamp(branchAt, 0f, branch.Length));
            Vector3 throatEnd = branch.GetPositionAtDistance(
                Mathf.Clamp(branchAt + branchSign * ThroatLength, 0f, branch.Length));

            float side = Vector3.Dot(throatEnd - mouthCentre, trunkRight) >= 0f ? 1f : -1f;

            // How far across the trunk's frame a point may not come. The paved edge, not the shoulder's:
            // AppendRing puts the camber at exactly zero there, so a throat clipped to this line meets
            // the carriageway flush to the millimetre and is offset from it only by the two centimetres
            // of Lift that every laid-on surface in this world carries.
            float keepOut = trunkShape.HalfWidth;

            Vector3 previousLeft = Vector3.zero;
            Vector3 previousRight = Vector3.zero;
            Vector3 previousUp = Vector3.up;
            bool have = false;

            for (int i = 0; i <= steps; i++)
            {
                float along = ThroatLength * i / steps;
                float half = HalfWidthAt(along, branchShape.HalfWidth, mouthHalfWidth);

                float at = Mathf.Clamp(branchAt + branchSign * along, 0f, branch.Length);

                Vector3 centre = branch.GetPositionAtDistance(at);
                Vector3 right = branch.GetBankedRightAtDistance(
                    at, branchShape.MaxBankDegrees, branchShape.FullBankRadius);

                Vector3 up = Vector3.Cross(branch.GetDirectionAtDistance(at), right).normalized;
                if (up.y < 0f)
                {
                    up = -up;
                }

                Vector3 surface = centre + up * (branchShape.SurfaceLift + MotorwayMergeBuilder.Lift);

                Vector3 left = surface - right * half;
                Vector3 outer = surface + right * half;

                // Smoothstep again, and for the same reason the flare uses one: a linear blend leaves a
                // crease across the throat at the point the trunk stops deciding its height.
                float blend = BlendLength <= 0f ? 1f : Mathf.Clamp01(along / BlendLength);
                blend = blend * blend * (3f - 2f * blend);

                left.y = Mathf.Lerp(PlaneHeight(left), left.y, blend);
                outer.y = Mathf.Lerp(PlaneHeight(outer), outer.y, blend);

                // And now the row is cut back to the edge of the road it is joining.
                //
                // <b>Without this the throat is laid across the carriageway, and on a circuit that is
                // across the racing line.</b> A row is square to the branch, so at the mouth — where the
                // branch's centreline is on the trunk's — it reaches mouthHalfWidth * cos(fork) to each
                // side of it: on the Weissjochring that is 5.2 m past the centreline of a carriageway
                // 6.5 m wide, ending in a square edge two centimetres proud, on the fastest part of the
                // lap. It was reported from the car as the pit road running onto the track.
                //
                // Covering the trunk was never what the throat was for. It exists because the branch's
                // own ribbon overlapped the trunk's — and the ribbon is trimmed back now
                // (RoadMeshBuilder.BuildRoad's from/to), so there is nothing left to cover. What is left
                // is a bell mouth that opens off the trunk's edge, which is what a fork looks like, and
                // the trunk keeps its markings, its camber and its kerbs.
                if (!ClipToTrunk(ref left, ref outer, trunkSurface, trunkRight, side, keepOut,
                        PlaneHeight))
                {
                    have = false;
                    continue;
                }

                if (have)
                {
                    // Outward is the surface normal, so the winding comes from the geometry rather than
                    // from which way round the two edges happened to be handed in.
                    into.AddQuadFacing(SurfaceSubmesh, previousLeft, previousRight, outer, left, previousUp);
                }

                previousLeft = left;
                previousRight = outer;
                previousUp = Vector3.Lerp(trunkUp, up, blend).normalized;
                have = true;
            }

            AppendFillets(trunk, trunkShape, trunkSurface, trunkRight, trunkUp,
                branch, branchShape, branchAt, branchSign, PlaneHeight, into);
        }

        /// <summary>
        /// Cuts one throat row back to the trunk's paved edge, in the trunk's own frame, and puts the
        /// cut end on the trunk's surface.
        ///
        /// <para>Returns false when the whole row is inside the carriageway, which restarts the strip
        /// rather than emitting a quad across a gap.</para>
        ///
        /// <para><b>The height is taken from the trunk, not carried over from the row.</b> A row is
        /// square to the branch, so the clip line runs <i>along</i> the trunk for forty metres or so of
        /// branch — and over that run the row's own height is blending off the trunk's plane onto the
        /// branch's grade. Interpolating the row's two ends and keeping the answer therefore walked the
        /// seam off the carriageway at the difference of the two grades: 19 cm on the Stadtfeld fork,
        /// 15 on the Weissjochring and <b>62 on the Bahçe Ring</b>, against a tolerance of four. Where
        /// this surface touches the trunk it is the trunk's height; the blend belongs on the far edge,
        /// which is where it goes, and the quad between the two is then the ruled apron
        /// <c>AppendFillets</c> already builds one road class out.</para>
        /// </summary>
        private static bool ClipToTrunk(
            ref Vector3 a, ref Vector3 b, Vector3 trunkSurface, Vector3 trunkRight, float side,
            float keepOut, System.Func<Vector3, float> planeHeight)
        {
            float da = Vector3.Dot(a - trunkSurface, trunkRight) * side - keepOut;
            float db = Vector3.Dot(b - trunkSurface, trunkRight) * side - keepOut;

            if (da >= 0f && db >= 0f)
            {
                return true;
            }

            if (da < 0f && db < 0f)
            {
                return false;
            }

            float t = da / (da - db);

            if (da < 0f)
            {
                a = Vector3.Lerp(a, b, t);
                a.y = planeHeight(a);
            }
            else
            {
                b = Vector3.Lerp(a, b, t);
                b.y = planeHeight(b);
            }

            return true;
        }

        /// <summary>
        /// The two corners: paving that carries the branch's edges round into the trunk's, so the fork
        /// reads as one piece of road rather than as two that happen to touch. See
        /// <see cref="FilletReach"/> for what this is fixing and how it was found.
        ///
        /// <para>Worked in the <b>trunk's</b> frame, which is what makes it simple enough to trust. The
        /// fork stands on straight and level track — <c>RoadCourseBuilder.AddJunction</c> requires it —
        /// so the trunk's paved edge is a straight line at a fixed distance across, and the nearest point
        /// on it to anything is that thing's own along-coordinate. No arc solve, no tangent points, and
        /// nothing that can fail quietly on a bend, because there is no bend.</para>
        /// </summary>
        private static void AppendFillets(
            IRoadPath trunk,
            in RoadShape trunkShape,
            Vector3 trunkSurface,
            Vector3 trunkRight,
            Vector3 trunkUp,
            IRoadPath branch,
            in RoadShape branchShape,
            float branchAt,
            float branchSign,
            System.Func<Vector3, float> planeHeight,
            VegetationMeshBuffer into)
        {
            float edge = trunkShape.OuterHalfWidth;

            int steps = Mathf.Max(2, Mathf.CeilToInt(FilletSearch / StepLength));

            // One pass per side of the branch. Both are needed and they are not symmetric: a branch
            // leaving at an angle has one edge that crosses the trunk's paving early and one that
            // crosses it late, so the two fillets are different sizes.
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;

                bool have = false;
                Vector3 previousInner = Vector3.zero;
                Vector3 previousOuter = Vector3.zero;

                for (int i = 0; i <= steps; i++)
                {
                    float along = FilletSearch * i / steps;
                    float at = Mathf.Clamp(branchAt + branchSign * along, 0f, branch.Length);

                    Vector3 centre = branch.GetPositionAtDistance(at);
                    Vector3 right = branch.GetBankedRightAtDistance(
                        at, branchShape.MaxBankDegrees, branchShape.FullBankRadius);

                    Vector3 up = Vector3.Cross(branch.GetDirectionAtDistance(at), right).normalized;
                    if (up.y < 0f)
                    {
                        up = -up;
                    }

                    Vector3 point = centre + right * (sign * branchShape.OuterHalfWidth);

                    // Where that lands in the trunk's frame, and therefore how much bare ground is
                    // between it and the trunk's paving.
                    Vector3 offset = point - trunkSurface;
                    float across = Vector3.Dot(offset, trunkRight);
                    float gap = Mathf.Abs(across) - edge;

                    // Still over the trunk's own carriageway: the throat has this covered.
                    if (gap <= 0f)
                    {
                        have = false;
                        continue;
                    }

                    if (gap >= FilletReach)
                    {
                        break;
                    }

                    // The point on the trunk's edge beside it, on the trunk's plane. Same along, pulled
                    // back across to the paved edge.
                    Vector3 onEdge = point - trunkRight * (across - Mathf.Sign(across) * edge);
                    onEdge.y = planeHeight(onEdge);

                    // The branch's own surface, not the trunk's plane. A fillet with both edges on the
                    // trunk's plane is flat, which is wrong the moment the branch has left it: the
                    // throat blends off that plane over BlendLength, so forty metres up a branch
                    // climbing at a per cent this laid a third of a metre of step along the inside of
                    // its own kerb line. Ruled between the two surfaces is what an apron is.
                    Vector3 inner = point + up * (branchShape.SurfaceLift + MotorwayMergeBuilder.Lift);

                    // Full width until the last few metres, then a smoothstep to nothing. See
                    // FilletReach for what tapering over the whole thing instead produced.
                    float t = Mathf.Clamp01((gap - (FilletReach - FilletTaper)) / FilletTaper);
                    float fill = 1f - t * t * (3f - 2f * t);

                    Vector3 reach = Vector3.Lerp(inner, onEdge, fill);

                    if (have)
                    {
                        into.AddQuadFacing(
                            SurfaceSubmesh, previousInner, previousOuter, reach, inner, trunkUp);
                    }

                    previousInner = inner;
                    previousOuter = reach;
                    have = true;
                }
            }
        }
    }
}
