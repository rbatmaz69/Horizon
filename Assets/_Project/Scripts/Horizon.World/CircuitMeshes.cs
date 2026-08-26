using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The paddock at the Weissjochring: the gantry over the start line, the pit garages behind it, a
    /// grandstand opposite, and the line and grid boxes painted on the road.
    ///
    /// <para><b>What this is for is telling a driver, in one frame, that they have arrived somewhere
    /// rather than at another stretch of mountain road.</b> The kerbs say "circuit" everywhere on the
    /// lap; this says "start here", and it is the only place on fifteen kilometres of road with a
    /// building on it.</para>
    ///
    /// <para><b>The board on the gantry is on a submesh of its own with a plain bright material and no
    /// <c>TownLights</c> registration.</b> Every <c>LitGroup</c> swaps between a day material and a
    /// night one, and a start/finish board is one of the things that has to look the same at midnight as
    /// at noon. Sharing the lit slot is what once painted a filling station's sign in road asphalt all
    /// day; the bridge beacons and the cable beads are on the same footing for the same reason.</para>
    ///
    /// <para><b>The line and the grid are laid-on geometry at <c>MotorwayMergeBuilder.Lift</c>, and that
    /// is only legitimate here because the main straight has no camber.</b> Laid-on paving sits flush
    /// only where the surface under it is flat across, which is why the circuit's course table holds the
    /// whole of the authored straight at zero grade and why the fork is on it too. Paint laid on a
    /// cambered road stands off it at the edges, which is the trap the town streets had to unpick.</para>
    /// </summary>
    public static class CircuitMeshes
    {
        /// <summary>Concrete: the pit block, the stand, the gantry legs' feet.</summary>
        public const int ConcreteSubmesh = 0;

        /// <summary>Painted steel: the gantry, the stand's roof.</summary>
        public const int MetalSubmesh = 1;

        /// <summary>The board. Bright, unlit, and never registered with <c>TownLights</c>.</summary>
        public const int BoardSubmesh = 2;

        /// <summary>Road paint: the line and the grid boxes.</summary>
        public const int PaintSubmesh = 3;

        public const int CircuitSubmeshCount = 4;

        /// <summary>Clear height under the gantry beam, metres. A gantry a car can hit is a bollard.</summary>
        private const float GantryClearance = 6.5f;

        private const float GantryLegWidth = 0.5f;
        private const float GantryBeamDepth = 0.9f;
        private const float BoardHeight = 1.6f;
        private const float BoardLength = 9f;

        /// <summary>How far the pit block's front stands off the centreline, metres.</summary>
        private const float PitStandoff = 26f;

        private const float PitLength = 130f;
        private const float PitDepth = 11f;
        private const float PitHeight = 5.5f;

        /// <summary>How far the grandstand stands off the centreline on the far side, metres.</summary>
        private const float StandStandoff = 24f;

        private const float StandLength = 96f;
        private const int StandRows = 6;
        private const float StandRowDepth = 1.4f;
        private const float StandRowRise = 0.85f;

        /// <summary>Width of the painted start/finish line, metres.</summary>
        private const float LineWidth = 0.9f;

        private const float GridBoxLength = 5.5f;
        private const float GridBoxWidth = 0.16f;

        /// <summary>
        /// How many starting slots there are, staggered either side of the centreline behind the line.
        ///
        /// <para>Twelve, which is six rows. It is a number the paint and the poses have to agree about,
        /// so both read it from here — see <see cref="GridSlot"/>.</para>
        /// </summary>
        public const int GridSlots = 12;

        /// <summary>Spacing of the grid rows along the road, metres.</summary>
        private const float GridPitch = 16f;

        /// <summary>
        /// Where one starting slot is, in the road's own frame.
        ///
        /// <para><b>One table, read twice.</b> The boxes painted on the road and the poses a car is put
        /// on are the same twelve places, and a second copy of this arithmetic anywhere would be twelve
        /// cars parked next to their boxes instead of on them — visible immediately and impossible to
        /// find, because both halves would look correct on their own. So the paint asks this, and so
        /// does whatever bakes the grid.</para>
        ///
        /// <para><b>Slots alternate sides <i>and</i> step back half a row each time, which is what makes
        /// it a grid rather than a row of pairs.</b> The first attempt advanced a row every two slots and
        /// left the odd ones level with the even ones — pole and second line abreast, six pairs of cars.
        /// The build said so in one number and nothing else would have: <c>0 m ahead of it</c>. Every car
        /// now sits in the gap the one in front left, which is the whole idea.</para>
        /// </summary>
        /// <param name="slot">0 is pole.</param>
        /// <param name="lineAt">Distance along the path the start line is painted at.</param>
        /// <param name="along">Distance along the path the slot sits at. May be negative on an open road.</param>
        /// <param name="across">Offset from the centreline, metres, signed.</param>
        public static void GridSlot(
            int slot, float lineAt, in RoadShape shape, out float along, out float across)
        {
            float side = slot % 2 == 0 ? -1f : 1f;

            along = lineAt - GridPitch * (slot / 2 + 1) - slot % 2 * GridPitch * 0.5f;
            across = shape.HalfWidth * 0.5f * side;
        }

        /// <summary>
        /// The structure's two colours — and <b>only</b> those two.
        ///
        /// <para><c>VegetationMeshBuffer.MergeTinted</c> does what it says: every slot carrying a tint is
        /// folded into the first one, with the colour baked into its vertices. That is exactly right for
        /// the concrete and the steel, which want one draw call and two colours. It is exactly wrong for
        /// the other two, which are here <i>because</i> they need a material of their own — the board a
        /// plain bright one that does not swap at dusk, the paint the road's own smoothness rather than
        /// a building's. Tinting them would have merged them into the structure and handed the lot to
        /// one material, which is the filling-station sign painted in road asphalt all over again, and
        /// the build would have said nothing louder than "1 of 4".</para>
        ///
        /// <para>So the rule that goes with this mechanism is not quite "never a null tint" — it is
        /// that a null tint means "keep this slot, it has a material of its own", and every slot has to
        /// mean one or the other on purpose.</para>
        /// </summary>
        public static Color?[] SurfaceTints()
        {
            var tints = new Color?[CircuitSubmeshCount];

            tints[ConcreteSubmesh] = new Color(0.62f, 0.60f, 0.57f);
            tints[MetalSubmesh] = new Color(0.28f, 0.30f, 0.33f);

            return tints;
        }

        /// <summary>
        /// Builds the paddock around the start/finish line.
        /// </summary>
        /// <param name="path">The circuit, sampled for the road's own frame at the line.</param>
        /// <param name="shape">Its cross-section: the gantry and the paint are sized off its width.</param>
        /// <param name="lineAt">Distance along <paramref name="path"/> the line is painted at.</param>
        /// <param name="inside">
        /// Which side the pits stand on: −1 left of travel, +1 right. The stand goes opposite. Passed in
        /// rather than worked out, because which side of a circuit is the infield is a fact about the
        /// course's plan and not about any one point on it.
        /// </param>
        public static void Append(
            IRoadPath path,
            in RoadShape shape,
            float lineAt,
            float inside,
            VegetationMeshBuffer into)
        {
            if (path == null || into == null)
            {
                return;
            }

            Vector3 at = path.GetPositionAtDistance(lineAt);
            Vector3 forward = path.GetDirectionAtDistance(lineAt);
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            AppendGantry(at, forward, right, shape, into);
            AppendPits(path, shape, lineAt, inside, into);
            AppendStand(path, shape, lineAt, -inside, into);
            AppendPaint(path, shape, lineAt, into);
        }

        /// <summary>
        /// The sector gates: a pair of white bands across the road at each one.
        ///
        /// <para>They are painted for one reason — a rule the player cannot see is a rule that reads as
        /// the game being broken. A lap that quietly refuses to count, with nothing on the road to say
        /// which gate was missed, is worse than no rule at all.</para>
        ///
        /// <para>Two bands rather than one, so a gate is not mistaken for the start line at a glance:
        /// the line is a single wide band under a gantry, and these are a pair of narrow ones with
        /// nothing over them.</para>
        /// </summary>
        public static void AppendGates(
            IRoadPath path, in RoadShape shape, float[] distances, VegetationMeshBuffer into)
        {
            if (path == null || distances == null || into == null)
            {
                return;
            }

            const float bandWidth = 0.5f;
            const float bandGap = 2f;

            for (int i = 0; i < distances.Length; i++)
            {
                float at = path.NormalizeDistance(distances[i]);

                AddStripe(path, shape, at - bandGap * 0.5f, bandWidth,
                    -shape.HalfWidth, shape.HalfWidth, into);

                AddStripe(path, shape, at + bandGap * 0.5f, bandWidth,
                    -shape.HalfWidth, shape.HalfWidth, into);
            }
        }

        /// <summary>The gantry: two legs outside the shoulders, a beam over, and the board on it.</summary>
        private static void AppendGantry(
            Vector3 at, Vector3 forward, Vector3 right, in RoadShape shape, VegetationMeshBuffer into)
        {
            float reach = shape.OuterHalfWidth + 1.2f;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                Vector3 foot = at + right * (reach * sign);

                AddBox(into, MetalSubmesh, foot, forward, right,
                    GantryLegWidth, GantryLegWidth, GantryClearance + GantryBeamDepth);

                // A plinth, so a steel leg does not appear to grow out of gravel.
                AddBox(into, ConcreteSubmesh, foot - Vector3.up * 0.4f, forward, right, 1f, 1f, 0.5f);
            }

            Vector3 beamFoot = at + Vector3.up * GantryClearance;
            AddBox(into, MetalSubmesh, beamFoot, right, forward, reach, GantryLegWidth * 0.6f,
                GantryBeamDepth);

            // The board sits on top of the beam and faces the road it spans, which is across `forward`.
            Vector3 boardFoot = at + Vector3.up * (GantryClearance + GantryBeamDepth);
            AddBox(into, BoardSubmesh, boardFoot, right, forward, BoardLength * 0.5f, 0.12f, BoardHeight);
        }

        /// <summary>The pit block, laid along the road on the infield side.</summary>
        private static void AppendPits(
            IRoadPath path, in RoadShape shape, float lineAt, float side, VegetationMeshBuffer into)
        {
            // Just past the line rather than behind it. Behind it is the closure's climbing approach,
            // and a hundred and thirty metres of level building on a seven per cent road is a building
            // with a hole under one end — the same reason the apron is centred where it is.
            float centreAt = lineAt + PitLength * 0.46f;

            Vector3 at = path.GetPositionAtDistance(WrapOn(path, centreAt));
            Vector3 forward = path.GetDirectionAtDistance(WrapOn(path, centreAt));
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            Vector3 foot = at + right * ((PitStandoff + PitDepth * 0.5f) * side);
            foot.y = at.y - shape.ShoulderDrop;

            AddBox(into, ConcreteSubmesh, foot, forward, right,
                PitLength * 0.5f, PitDepth * 0.5f, PitHeight);

            // A parapet band along the top, which is most of what stops a long block reading as a crate.
            Vector3 band = foot + Vector3.up * PitHeight;
            AddBox(into, MetalSubmesh, band, forward, right,
                PitLength * 0.5f + 0.4f, PitDepth * 0.5f + 0.4f, 0.7f);
        }

        /// <summary>The grandstand: a rake of steps facing the road, with a roof over the back of it.</summary>
        private static void AppendStand(
            IRoadPath path, in RoadShape shape, float lineAt, float side, VegetationMeshBuffer into)
        {
            Vector3 at = path.GetPositionAtDistance(lineAt);
            Vector3 forward = path.GetDirectionAtDistance(lineAt);
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            float baseY = at.y - shape.ShoulderDrop;

            for (int row = 0; row < StandRows; row++)
            {
                float across = StandStandoff + row * StandRowDepth;

                Vector3 foot = at + right * ((across + StandRowDepth * 0.5f) * side);
                foot.y = baseY;

                // Each step is a full-height box up from the ground rather than a slab on the one
                // behind it: the rake is then a solid mass with a stepped face, which is what a
                // concrete stand is, and there is no hollow underneath for the camera to see into.
                AddBox(into, ConcreteSubmesh, foot, forward, right,
                    StandLength * 0.5f, StandRowDepth * 0.5f, (row + 1) * StandRowRise);
            }

            float backAcross = StandStandoff + StandRows * StandRowDepth;
            Vector3 roofFoot = at + right * ((backAcross * 0.5f + StandStandoff * 0.5f) * side);
            roofFoot.y = baseY + StandRows * StandRowRise + 3.4f;

            AddBox(into, MetalSubmesh, roofFoot, forward, right,
                StandLength * 0.5f + 1f, (backAcross - StandStandoff) * 0.5f + 2f, 0.35f);

            // Two posts holding it, at the ends. Two rather than a row: it is seen from one side at
            // speed, and a colonnade here is triangles nobody looks at.
            for (int end = 0; end < 2; end++)
            {
                float along = (end == 0 ? -1f : 1f) * (StandLength * 0.5f - 2f);
                Vector3 post = roofFoot + forward * along;
                post.y = baseY;

                AddBox(into, MetalSubmesh, post, forward, right, 0.22f, 0.22f,
                    StandRows * StandRowRise + 3.4f);
            }
        }

        /// <summary>The start/finish line and the grid boxes, laid on the carriageway.</summary>
        private static void AppendPaint(
            IRoadPath path, in RoadShape shape, float lineAt, VegetationMeshBuffer into)
        {
            AddStripe(path, shape, lineAt, LineWidth, -shape.HalfWidth, shape.HalfWidth, into);

            for (int i = 0; i < GridSlots; i++)
            {
                GridSlot(i, lineAt, shape, out float behind, out float centre);

                AddStripe(path, shape, behind, GridBoxLength, centre - 1.5f, centre + 1.5f, into,
                    hollow: true);
            }
        }

        /// <summary>
        /// A rectangle of paint on the road surface, given in the road's own across/along frame.
        /// </summary>
        /// <param name="hollow">
        /// Draw only the two rails of the box rather than filling it. A grid box is an outline; filled,
        /// it is a white slab across the racing line.
        /// </param>
        private static void AddStripe(
            IRoadPath path,
            in RoadShape shape,
            float at,
            float alongLength,
            float acrossFrom,
            float acrossTo,
            VegetationMeshBuffer into,
            bool hollow = false)
        {
            float half = alongLength * 0.5f;

            float back = WrapOn(path, at - half);
            float front = WrapOn(path, at + half);

            Vector3 a = Surface(path, shape, back, acrossFrom);
            Vector3 b = Surface(path, shape, back, acrossTo);
            Vector3 c = Surface(path, shape, front, acrossTo);
            Vector3 d = Surface(path, shape, front, acrossFrom);

            if (!hollow)
            {
                // Subdivided across the road rather than laid as one quad, and this is the whole
                // reason the paint is visible at all.
                //
                // <b>The carriageway is crowned.</b> Surface() lifts a point by Crown·(1−t²) with t the
                // fraction of the half-width, so a single quad spanning both edges is a chord drawn
                // under a parabola: it meets the tarmac at the two edges and sits the full Crown —
                // eleven centimetres — *below* it on the centreline. Every triangle builds, the count
                // in the log is right, and the band is invisible except for a sliver at each kerb.
                // That is how the start/finish line and all six sector gates came to be buried on both
                // circuits, which is worse than not drawing them: a rule the player cannot see reads as
                // the game being broken, and the gates exist to be seen.
                //
                // Eight spans leaves Crown/256 of sag — a third of a millimetre. It is the same trap as
                // the grid boxes' inverted crown, one step further on: getting the sign right is not
                // enough if the shape is not followed as well.
                const int spans = 8;

                for (int i = 0; i < spans; i++)
                {
                    float from = Mathf.Lerp(acrossFrom, acrossTo, i / (float)spans);
                    float to = Mathf.Lerp(acrossFrom, acrossTo, (i + 1) / (float)spans);

                    into.AddQuadFacing(
                        PaintSubmesh,
                        Surface(path, shape, back, from),
                        Surface(path, shape, back, to),
                        Surface(path, shape, front, to),
                        Surface(path, shape, front, from),
                        Vector3.up);
                }

                return;
            }

            // The two long rails only. The ends are open, which is what a grid box looks like from a
            // car and costs four triangles instead of eight.
            const float rail = GridBoxWidth;

            Vector3 a2 = Surface(path, shape, back, acrossFrom + rail);
            Vector3 d2 = Surface(path, shape, front, acrossFrom + rail);
            into.AddQuadFacing(PaintSubmesh, a, a2, d2, d, Vector3.up);

            Vector3 b2 = Surface(path, shape, back, acrossTo - rail);
            Vector3 c2 = Surface(path, shape, front, acrossTo - rail);
            into.AddQuadFacing(PaintSubmesh, b2, b, c, c2, Vector3.up);
        }

        /// <summary>A point on the carriageway, lifted clear of it by the amount laid-on paving uses.</summary>
        private static Vector3 Surface(IRoadPath path, in RoadShape shape, float at, float across)
        {
            Vector3 centre = path.GetPositionAtDistance(at);
            Vector3 right = path.GetBankedRightAtDistance(at, shape.MaxBankDegrees, shape.FullBankRadius);

            Vector3 point = centre + right * across;

            // <b>The crown rises towards the middle; it does not fall away from it.</b> Read
            // RoadMeshBuilder.AppendRing: the section's rise is +Crown on the centreline, three quarters
            // of it at the quarter points and zero at the asphalt edges — the road is a shallow ridge,
            // not a shallow dish. Signed the other way round, as this was, the paint sits eleven
            // centimetres inside the tarmac down the middle of the lane and only breaks the surface at
            // the very edge. Which is what happened: the start line and all twelve grid boxes were
            // built, counted correctly in the log at fifty triangles, and completely invisible.
            //
            // This is the specific shape of the trap the codebase already records as "laid-on paving
            // only sits flush where the surface under it has no camber to follow". Getting the camber's
            // *sign* wrong is worse than ignoring it: ignoring it leaves the paint floating at the
            // edges, where it can at least be seen.
            float t = shape.HalfWidth > 0.01f ? across / shape.HalfWidth : 0f;
            point.y += shape.Crown * (1f - t * t);

            // And on top of the ribbon's own lift, not just of the path it is swept along.
            point.y += shape.SurfaceLift + MotorwayMergeBuilder.Lift;

            return point;
        }

        /// <summary>
        /// Distance clamped or wrapped as the path itself would.
        ///
        /// <para>It has to wrap, and this is the one place in the paddock where that matters: the start
        /// line sits at distance zero on a closed course, so half the grid is at a *negative* distance —
        /// which on a loop means the far end of the main straight and on anything else means the start
        /// of the road. Clamping here would stack six grid boxes on top of each other at the line.</para>
        /// </summary>
        private static float WrapOn(IRoadPath path, float at)
        {
            return path.NormalizeDistance(at);
        }

        /// <summary>
        /// An oriented box with all six faces. Its own rather than <c>BuildingMeshes.AddBox</c>'s,
        /// because that one works in a <c>PlantPlacement</c>'s local frame and everything here is placed
        /// against a road's forward and right instead.
        /// </summary>
        private static void AddBox(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 foot,
            Vector3 forward,
            Vector3 outward,
            float halfLength,
            float halfDepth,
            float height)
        {
            Vector3 a = forward * halfLength;
            Vector3 d = outward * halfDepth;
            Vector3 up = Vector3.up * height;

            Vector3 b0 = foot - a - d;
            Vector3 b1 = foot + a - d;
            Vector3 b2 = foot + a + d;
            Vector3 b3 = foot - a + d;

            Vector3 t0 = b0 + up;
            Vector3 t1 = b1 + up;
            Vector3 t2 = b2 + up;
            Vector3 t3 = b3 + up;

            buffer.AddQuadFacing(submesh, b0, b1, t1, t0, -outward);
            buffer.AddQuadFacing(submesh, b2, b3, t3, t2, outward);
            buffer.AddQuadFacing(submesh, b1, b2, t2, t1, forward);
            buffer.AddQuadFacing(submesh, b3, b0, t0, t3, -forward);
            buffer.AddQuadFacing(submesh, t0, t1, t2, t3, Vector3.up);
            buffer.AddQuadFacing(submesh, b3, b2, b1, b0, Vector3.down);
        }
    }
}
