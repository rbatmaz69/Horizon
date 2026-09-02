using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// The dimensions of one suspension structure, handed to
    /// <see cref="SuspensionBridgeBuilder"/> rather than owned by it.
    ///
    /// <para><b>Passed in, the way <see cref="RoadShape"/> is.</b> Three separate things have to agree
    /// about where the towers stand: the course, which puts the anchorages on dry land by choosing how
    /// long the structure is; the channel, whose half-width decides whether the towers rise out of
    /// water or out of a field; and this builder. A constant living here would be a fourth opinion, and
    /// the first three are the ones that can be wrong in a way nobody notices.</para>
    /// </summary>
    public readonly struct SuspensionShape
    {
        /// <summary>From an anchorage to the tower in front of it, metres.</summary>
        public readonly float SideSpan;

        /// <summary>How far a tower head stands above the deck, metres.</summary>
        public readonly float TowerRise;

        /// <summary>How far the main cable hangs below the tower heads at mid-span, metres.</summary>
        public readonly float CableSag;

        public SuspensionShape(float sideSpan, float towerRise, float cableSag)
        {
            SideSpan = sideSpan;
            TowerRise = towerRise;
            CableSag = cableSag;
        }
    }

    /// <summary>
    /// Carries a carriageway across open water: two towers, a cable slung between them, hangers down to
    /// the deck, and an anchorage at each end.
    ///
    /// <para><b>Why this is not <see cref="BridgeBuilder"/> with a longer span.</b> That one plants a
    /// pier pair every forty metres, sized against the ground under each leg. Over a shipping channel
    /// there is no ground worth standing on for nine hundred metres, and twenty-three pier pairs marching
    /// across it is the structure this file exists to avoid. Everything else the two have in common they
    /// share rather than copy: the deck girder and the parapet come straight out of
    /// <see cref="BridgeBuilder"/>, because a deck is a deck whatever is holding it up.</para>
    ///
    /// <para><b>Measured first and built second</b>, the two-pass shape
    /// <see cref="GuardRailBuilder"/> established and for a sharper reason here than anywhere else: a
    /// hanger's length is the gap between two curves that do not exist until both have been walked, and
    /// a hanger computed from the deck alone is a guess about where the cable went.</para>
    ///
    /// <para><b>The cable is a parabola sampled along the road, not a curve drawn between two points in
    /// space.</b> Every station is taken from <c>path.GetPositionAtDistance</c> and offset in that
    /// station's own frame, so a crossing that is ever given a bend or a camber keeps its cables over
    /// its parapets instead of beside them. On the dead-straight, dead-level span this is built for the
    /// two are identical — which is exactly when it is cheap to get right.</para>
    ///
    /// <para><b>The lights are on a submesh of their own with no <c>TownLights</c> registration</b>, for
    /// the reason recorded against the filling stations: every lit group swaps between a day material
    /// and a night one, and a tower head beacon that goes out at noon is a tower head beacon that spends
    /// half the day painted road-asphalt. A bridge lit along its cables is most of why anybody photographs
    /// one, so those beads keep a plain bright material and look the same at both ends of the day.</para>
    ///
    /// <para>The half that is not in this file is the same half that is missing from
    /// <see cref="BridgeBuilder"/>: the water and the missing ground.
    /// <see cref="RoadFeatureKind.Suspension"/> is what makes <see cref="MountainField"/> drop its shelf
    /// across the span. Build one of these without marking the feature and you get a very elegant
    /// structure standing on a causeway.</para>
    /// </summary>
    public static class SuspensionBridgeBuilder
    {
        /// <summary>Deck girder, anchor blocks, and the towers below deck level. Same slot as a viaduct's.</summary>
        public const int ConcreteSubmesh = BridgeBuilder.ConcreteSubmesh;

        /// <summary>Parapets, tower shafts, cross-beams, cables and hangers. Same slot as a viaduct's parapet.</summary>
        public const int SteelSubmesh = BridgeBuilder.ParapetSubmesh;

        /// <summary>The beacons and the cable beads. See the class remarks — never a lit group.</summary>
        public const int LampSubmesh = 2;

        private const int SubmeshCount = 3;

        /// <summary>Stations along the main cable. Fine enough that the parabola does not read as a chain.</summary>
        private const int CableSteps = 48;

        /// <summary>Spacing of the hanger pairs, metres.</summary>
        private const float HangerSpacing = 22f;

        /// <summary>A hanger shorter than this is not built. Near a tower the cable is already at the deck.</summary>
        private const float MinimumHanger = 2.5f;

        /// <summary>Half-section of the main cable, metres.</summary>
        private const float CableHalf = 0.55f;

        /// <summary>Half-section of a hanger.</summary>
        private const float HangerHalf = 0.11f;

        /// <summary>
        /// How far outside the carriageway edge the cable plane runs.
        ///
        /// <para>Outside the parapet rather than over it, so a hanger comes down past the rail instead of
        /// through it. Small enough that from the deck the cables still read as belonging to the road
        /// rather than as two fences beside it.</para>
        /// </summary>
        private const float CableOffset = 1.13f;

        // Scaled with the deck when the roads were widened for the cars. A tower is read against the
        // carriageway running between its legs, and a 3 m leg either side of a 21 m deck is the same
        // structure the 2.4 m one was beside a 17 m deck.
        private const float TowerHalfAcross = 3f;
        private const float TowerHalfAlong = 4f;

        /// <summary>
        /// How much wider than the carriageway the deck is, each side, metres.
        ///
        /// <para><b>This is what gives the towers and the anchor blocks somewhere to stand that is not
        /// the road.</b> Every structural offset here used to be
        /// <c>roadShape.OuterHalfWidth + CableOffset</c> — one number, sized for a cable half a metre
        /// thick and then handed to bodies several metres across. The tower foundation's inner face
        /// landed exactly on the edge of the asphalt and the anchor block's landed two metres inside the
        /// lane, as a seven-metre concrete wall on both sides of the entrance: six metres of clear
        /// width on a road that was thirteen and a half wide, at the two points on the crossing where a
        /// driver is already busy.</para>
        ///
        /// <para>Widening the deck is the honest fix rather than shuffling the blocks outboard of a
        /// deck they would then overhang. A suspension deck <i>is</i> wider than the road on it —
        /// footways, refuges, the stiffening girder's own width — and every real crossing puts its
        /// towers in that margin. 3.25 m each side clears the 3 m tower half-section with room, and
        /// puts the cable planes 25.6 m apart, which still reads as a bridge from the deck rather than
        /// as two fences beside the road.</para>
        ///
        /// <para>It was 2.6 against a 6.75 m road and is 3.25 against the 8.5 m one the carriageways
        /// became. Everything structural here is derived from <see cref="DeckShapeFor"/>, so the towers,
        /// the cables, the hangers and the anchor axis all moved with it and
        /// <c>ValidateSuspensionBridges</c>' fifth question — does any of this stand in the road it is
        /// carrying — is what confirms they did.</para>
        /// </summary>
        private const float DeckOverhang = 3.25f;

        /// <summary>Gap between the parapet's outer face and an anchor block's inner face, metres.</summary>
        private const float AnchorClearance = 0.6f;

        /// <summary>How far a tower foot is sunk below whatever it lands on, so it never floats.</summary>
        private const float FootBurial = 2.5f;

        /// <summary>Height of the lower cross-beam above the deck, and of the upper one below the head.</summary>
        private const float LowerBeam = 12f;

        private const float UpperBeamDrop = 9f;

        private const float BeamHalfDepth = 1.4f;

        private const float AnchorHalfWidth = 5.6f;
        private const float AnchorHalfDepth = 7.5f;
        private const float AnchorHeight = 7f;

        /// <summary>How far below an anchor block's top face the back-stay enters it, metres.</summary>
        private const float AnchorTopDrop = 1.2f;

        /// <summary>
        /// Spacing of the lamp beads along a cable, metres.
        ///
        /// <para>Twenty-six, down from forty-four. At the wider spacing the night shot came back with
        /// four visible lights on a kilometre of cable, which is a dark bridge with a fault on it rather
        /// than a lit one — and a bridge lit along its cables is most of the reason anybody photographs
        /// one. The beads are cheap: two dozen boxes a side against five thousand triangles of
        /// structure.</para>
        /// </summary>
        private const float BeadSpacing = 26f;

        private const float BeadHalf = 0.5f;

        /// <summary>Spacing of the lamp standards along the deck, metres.</summary>
        private const float LampSpacing = 48f;

        /// <summary>Height of a lamp standard above the parapet coping.</summary>
        private const float LampHeight = 5.2f;

        /// <summary>
        /// Builds every suspension span on a course as one mesh, or returns null if it has none.
        ///
        /// <para>One mesh for all of them, as the viaducts are, and for the same reason: a few thousand
        /// triangles is cheaper as one draw call than as streaming bookkeeping. Unlike a viaduct this one
        /// should never be streamed out at all — it is the tallest thing in the world and visible from
        /// the far side of it — so its chunk is given a radius that keeps it resident.</para>
        /// </summary>
        /// <param name="path">The carriageway being carried. One structure per carriageway.</param>
        /// <param name="course">Read for its <see cref="RoadFeatureKind.Suspension"/> spans.</param>
        /// <param name="shape">See <see cref="SuspensionShape"/> for why this is not a set of constants.</param>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            in SuspensionShape shape,
            List<int> usedSubmeshes,
            string meshName = "SuspensionBridgeMesh",
            List<float> supports = null)
        {
            if (course == null)
            {
                return null;
            }

            var buffer = new VegetationMeshBuffer(SubmeshCount);
            bool any = false;

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];

                if (feature.Kind != RoadFeatureKind.Suspension || feature.Length <= 1f)
                {
                    continue;
                }

                AddStructure(buffer, path, roadShape, field, feature, shape, supports);
                any = true;
            }

            return any ? buffer.ToMesh(meshName, usedSubmeshes) : null;
        }

        /// <summary>
        /// The deck this structure carries, which is wider than the road on it — see
        /// <see cref="DeckOverhang"/>.
        ///
        /// <para>One function rather than a line in <see cref="AddStructure"/>, because three things
        /// have to agree about it: what is built, what is solid, and what
        /// <c>ValidateSuspensionBridges</c> measures the clear width against. That is the same reason
        /// <c>SuspensionShape</c> is passed in rather than declared here.</para>
        /// </summary>
        public static RoadShape DeckShapeFor(in RoadShape roadShape)
        {
            RoadShape deck = roadShape;
            deck.ShoulderWidth += DeckOverhang;
            return deck;
        }

        /// <summary>Where the cable plane, and therefore each tower, stands.</summary>
        public static float CableAxis(in RoadShape roadShape)
        {
            return DeckShapeFor(roadShape).OuterHalfWidth + CableOffset;
        }

        /// <summary>Where an anchor block's centre stands. Its own axis, not the cable's — see the class remarks.</summary>
        public static float AnchorAxis(in RoadShape roadShape)
        {
            return DeckShapeFor(roadShape).OuterHalfWidth + BridgeBuilder.ParapetThickness
                   + AnchorClearance + AnchorHalfWidth;
        }

        /// <summary>The inner face of each structural family, for the clear-width check to walk.</summary>
        public static void InnerFaces(
            in RoadShape roadShape,
            out float towerFoundation,
            out float towerShaft,
            out float anchorBlock)
        {
            float cable = CableAxis(roadShape);
            towerFoundation = cable - TowerHalfAcross;
            towerShaft = cable - TowerHalfAcross * 0.8f;
            anchorBlock = AnchorAxis(roadShape) - AnchorHalfWidth;
        }

        /// <summary>
        /// The collision wall along the crossing's parapets.
        ///
        /// <para>Its own method rather than <see cref="BridgeBuilder.BuildParapetCollision"/> for one
        /// reason: the parapet on this deck stands on <see cref="DeckShapeFor"/>'s wider edge, and a
        /// wall built from the carriageway's width would be a solid barrier two and a half metres
        /// inside the one the driver can see.</para>
        /// </summary>
        public static Mesh BuildParapetCollision(
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            string meshName = "SuspensionParapetCollisionMesh")
        {
            if (course == null)
            {
                return null;
            }

            RoadShape deckShape = DeckShapeFor(roadShape);
            var buffer = new VegetationMeshBuffer(1);

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind != RoadFeatureKind.Suspension || feature.Length <= 1f)
                {
                    continue;
                }

                BridgeBuilder.SampleBarrierLine(path, deckShape,
                    feature.StartDistance, feature.EndDistance,
                    out Vector3[] bases, out Vector3[] rights, out Vector3[] ups);

                BridgeBuilder.AddBarrier(buffer, bases, rights, ups,
                    deckShape.OuterHalfWidth,
                    deckShape.OuterHalfWidth + BridgeBuilder.ParapetThickness,
                    BridgeBuilder.ParapetHeight);
            }

            return buffer.IsEmpty ? null : buffer.ToMesh(meshName, new List<int>(1));
        }

        private static void AddStructure(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            in RoadFeature feature,
            in SuspensionShape shape,
            List<float> supports)
        {
            // --- The deck, exactly as a viaduct's is, except that it is wider than the road on it.
            RoadShape deckShape = DeckShapeFor(roadShape);

            float span = feature.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(span / BridgeBuilder.DeckStep) + 1);

            var centres = new Vector3[steps];
            var rights = new Vector3[steps];
            var ups = new Vector3[steps];

            for (int step = 0; step < steps; step++)
            {
                float distance = feature.StartDistance + span * step / (steps - 1);
                Sample(path, deckShape, distance, out centres[step], out rights[step], out ups[step]);
            }

            BridgeBuilder.AddGirder(buffer, deckShape, centres, rights, ups);

            // The margin the widening opened up, laid as slab. Without it the road ribbon stops at the
            // asphalt edge and the parapet stands 2.6 m further out over nothing — the girder has no
            // top face, the carriageway is its top face.
            BridgeBuilder.AddFootways(buffer, roadShape.OuterHalfWidth, deckShape.OuterHalfWidth,
                deckShape, centres, rights, ups);

            BridgeBuilder.AddParapets(buffer, deckShape, centres, rights, ups);

            // --- Where the towers stand. Clamped rather than trusted: a structure authored shorter than
            // two side spans would otherwise put its towers the wrong way round, which is a silent and
            // very confusing mesh.
            float sideSpan = Mathf.Min(shape.SideSpan, span * 0.4f);
            float westTower = feature.StartDistance + sideSpan;
            float eastTower = feature.EndDistance - sideSpan;
            float mainSpan = eastTower - westTower;

            // Three axes, not one. See DeckOverhang for what having only one of them cost.
            float cableOffset = CableAxis(roadShape);
            float anchorOffset = AnchorAxis(roadShape);

            // The blocks sit behind the deck ends rather than across them, so what stands beside the
            // approach road is the block's flank and not its corner.
            float westAnchor = feature.StartDistance - AnchorHalfDepth;
            float eastAnchor = feature.EndDistance + AnchorHalfDepth;

            AddTower(buffer, path, deckShape, field, westTower, cableOffset, shape.TowerRise);
            AddTower(buffer, path, deckShape, field, eastTower, cableOffset, shape.TowerRise);

            AddAnchorage(buffer, path, deckShape, field, westAnchor, anchorOffset);
            AddAnchorage(buffer, path, deckShape, field, eastAnchor, anchorOffset);

            // --- The side spans. A hundred and fifty metres of deck each, and until this they were held
            // up by nothing at all: BridgeBuilder takes only RoadFeatureKind.Bridge, so no pier was ever
            // built here, and the hangers hung from the main cable and therefore only between the
            // towers. MountainField had meanwhile carved its nine metres of headroom under the whole
            // structure, both kinds being IsBridged. The result was the road arriving at the crossing
            // over an open hole. Piers where the ground carries one, hangers off the back-stay
            // everywhere: no bay is now without something holding it.
            BridgeBuilder.AddPiers(buffer, path, deckShape, field,
                feature.StartDistance, westTower, supports);
            BridgeBuilder.AddPiers(buffer, path, deckShape, field,
                eastTower, feature.EndDistance, supports);

            supports?.Add(westTower);
            supports?.Add(eastTower);
            supports?.Add(feature.StartDistance);
            supports?.Add(feature.EndDistance);

            // Height of the back-stay over the deck at each of its ends, so the side-span hangers can be
            // hung from the same straight line the cable itself is drawn along.
            float anchorRise = AnchorHeight - AnchorTopDrop;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;

                Vector3[] cable = MainCable(
                    path, deckShape, westTower, mainSpan, sign * cableOffset, shape);

                AddTube(buffer, SteelSubmesh, cable, CableHalf);

                AddBackstay(buffer, path, deckShape, westTower, westAnchor,
                    sign * cableOffset, sign * anchorOffset, shape.TowerRise);
                AddBackstay(buffer, path, deckShape, eastTower, eastAnchor,
                    sign * cableOffset, sign * anchorOffset, shape.TowerRise);

                // Main span: cable to parapet, the parabola's sag included.
                AddHangers(buffer, path, deckShape, westTower, eastTower,
                    cableOffset, cableOffset, shape.TowerRise, shape.TowerRise,
                    shape.CableSag, sign, supports);

                // Side spans: back-stay to parapet. The line is straight, so no sag, and both its
                // offset and its height are interpolated because the stay splays outwards on its way
                // to an anchor block that no longer stands on the cable plane.
                AddSideHangers(buffer, path, deckShape, westTower, feature.StartDistance, westAnchor,
                    cableOffset, anchorOffset, shape.TowerRise, anchorRise, sign, supports);
                AddSideHangers(buffer, path, deckShape, eastTower, feature.EndDistance, eastAnchor,
                    cableOffset, anchorOffset, shape.TowerRise, anchorRise, sign, supports);

                AddBeads(buffer, cable);
                AddDeckLamps(buffer, path, deckShape, feature, sign);
            }
        }

        /// <summary>
        /// The hangers over one side span, taken off the back-stay rather than off the main cable.
        ///
        /// <para>Works in the stay's own parameter and then clips to the deck: the stay runs from the
        /// tower to an anchor block that stands <i>behind</i> the abutment, so the last stretch of it is
        /// over ground rather than over deck, and a hanger there would be a rod into the hillside.</para>
        /// </summary>
        private static void AddSideHangers(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape deckShape,
            float towerDistance,
            float deckEndDistance,
            float anchorDistance,
            float towerOffset,
            float anchorOffset,
            float towerRise,
            float anchorRise,
            float sign,
            List<float> supports)
        {
            float stay = anchorDistance - towerDistance;
            if (Mathf.Abs(stay) < 0.01f)
            {
                return;
            }

            // How far along the stay the deck runs out.
            float atDeckEnd = (deckEndDistance - towerDistance) / stay;

            AddHangers(buffer, path, deckShape, towerDistance, deckEndDistance,
                towerOffset, Mathf.Lerp(towerOffset, anchorOffset, atDeckEnd),
                towerRise, Mathf.Lerp(towerRise, anchorRise, atDeckEnd),
                0f, sign, supports);
        }

        /// <summary>The road's frame, which is <see cref="BridgeBuilder.Sample"/> — a deck is a deck.</summary>
        private static void Sample(
            IRoadPath path,
            in RoadShape roadShape,
            float distance,
            out Vector3 centre,
            out Vector3 right,
            out Vector3 up)
        {
            BridgeBuilder.Sample(path, roadShape, distance, out centre, out right, out up);
        }

        /// <summary>
        /// The main cable: a parabola between the tower heads, sampled in the road's own frame.
        ///
        /// <para>Sag is measured down from the heads, so <c>drop(t) = 4·sag·t·(1−t)</c> — zero at each
        /// tower and a full sag at the middle. Everything else about the curve comes from the deck it is
        /// following.</para>
        /// </summary>
        private static Vector3[] MainCable(
            IRoadPath path,
            in RoadShape roadShape,
            float westTower,
            float mainSpan,
            float offset,
            in SuspensionShape shape)
        {
            var points = new Vector3[CableSteps + 1];

            for (int i = 0; i <= CableSteps; i++)
            {
                float t = i / (float)CableSteps;
                float drop = 4f * shape.CableSag * t * (1f - t);

                Sample(path, roadShape, westTower + mainSpan * t,
                    out Vector3 centre, out Vector3 right, out Vector3 up);

                points[i] = centre + right * offset + up * (shape.TowerRise - drop);
            }

            return points;
        }

        /// <summary>
        /// One tower: two shafts standing on whatever is under them, and two cross-beams between.
        ///
        /// <para>The shafts are sunk to <see cref="MountainField.HeightAt"/> at their own feet, which
        /// over the channel is the carved bed rather than the natural ground — so a tower in the water
        /// reaches the bottom of the water and one on a bank reaches the bank, with nothing here having
        /// to know which it is.</para>
        /// </summary>
        private static void AddTower(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            float distance,
            float offset,
            float towerRise)
        {
            Sample(path, roadShape, distance, out Vector3 centre, out Vector3 right, out Vector3 up);

            Vector3 along = Vector3.Cross(up, right).normalized;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                Vector3 axis = centre + right * (offset * sign);

                float ground = field.HeightAt(axis.x, axis.z) - FootBurial;
                var foot = new Vector3(axis.x, ground, axis.z);
                Vector3 head = axis + up * towerRise;

                // Below the deck it is a foundation and above it a shaft, and they are one box: a
                // suspension tower is continuous through the deck it carries, and splitting it would put
                // a seam exactly where the eye goes.
                AddPrismBetween(buffer, ConcreteSubmesh, foot, new Vector3(axis.x, axis.y, axis.z),
                    right, along, TowerHalfAcross, TowerHalfAlong);

                AddPrismBetween(buffer, SteelSubmesh, axis, head,
                    right, along, TowerHalfAcross * 0.8f, TowerHalfAlong * 0.75f);

                // The beacon. Aircraft warning on a real one; here it is what makes the tower head read
                // at night from the corniche, which is the only place the whole structure is in frame.
                AddBox(buffer, LampSubmesh, head + up * 0.9f, right, along, up, 0.7f, 1.4f, 0.7f);
            }

            // Cross-beams. Two, because one reads as a ladder and three as scaffolding.
            AddBeam(buffer, centre, right, along, up, offset, LowerBeam);
            AddBeam(buffer, centre, right, along, up, offset, towerRise - UpperBeamDrop);
        }

        private static void AddBeam(
            VegetationMeshBuffer buffer,
            Vector3 centre,
            Vector3 right,
            Vector3 along,
            Vector3 up,
            float offset,
            float height)
        {
            Vector3 seat = centre + up * height;
            AddBox(buffer, SteelSubmesh, seat, right, along, up,
                offset + TowerHalfAcross * 0.8f, 2.2f, BeamHalfDepth);
        }

        /// <summary>
        /// The anchor block, and the one piece of a suspension bridge that has to be on land.
        ///
        /// <para>It is a lump of concrete rather than anything shaped, which is what a real one is. What
        /// matters is that it stands where the back-stay lands and is buried into the ground behind the
        /// abutment, so the cable ends in something rather than in the air.</para>
        /// </summary>
        private static void AddAnchorage(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            float distance,
            float offset)
        {
            Sample(path, roadShape, distance, out Vector3 centre, out Vector3 right, out Vector3 up);
            Vector3 along = Vector3.Cross(up, right).normalized;

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                Vector3 seat = centre + right * (offset * sign);

                float ground = Mathf.Min(field.HeightAt(seat.x, seat.z), seat.y) - FootBurial;
                var foot = new Vector3(seat.x, ground, seat.z);
                Vector3 top = seat + up * AnchorHeight;

                AddPrismBetween(buffer, ConcreteSubmesh, foot, top, right, along,
                    AnchorHalfWidth, AnchorHalfDepth);
            }
        }

        /// <summary>
        /// The back-stay: tower head to anchor block, dead straight, which is what one is.
        ///
        /// <para>Two offsets rather than one, because the block no longer stands on the cable plane —
        /// it is out at <see cref="AnchorAxis"/> so that it clears the carriageway. The stay therefore
        /// splays four and a half metres outwards over a hundred and fifty, which is a real bridge's
        /// geometry and invisible from the deck.</para>
        /// </summary>
        private static void AddBackstay(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape deckShape,
            float towerDistance,
            float anchorDistance,
            float towerOffset,
            float anchorOffset,
            float towerRise)
        {
            Sample(path, deckShape, towerDistance, out Vector3 tc, out Vector3 tr, out Vector3 tu);
            Sample(path, deckShape, anchorDistance, out Vector3 ac, out Vector3 ar, out Vector3 au);

            Vector3 head = tc + tr * towerOffset + tu * towerRise;
            Vector3 anchor = ac + ar * anchorOffset + au * (AnchorHeight - AnchorTopDrop);

            AddTube(buffer, SteelSubmesh, new[] { head, anchor }, CableHalf);
        }

        /// <summary>
        /// The hangers between one pair of stations, and the reason this builder measures before it
        /// builds: each one is the gap between a cable and the parapet at the same station, and neither
        /// is known until both curves have been walked.
        ///
        /// <para><b>One method for the main span and the side spans</b>, which differ only in what the
        /// line overhead is doing — a parabola between two tower heads at one height, or a straight stay
        /// running out and down to an anchor block. Both are an interpolated offset and an interpolated
        /// rise with a sag term that is zero for one of them, so the second case is arguments rather
        /// than a second loop that would drift from this one.</para>
        ///
        /// <para>The foot lands on the parapet's inner face and the head on the cable plane, so a hanger
        /// leans outward by <see cref="CableOffset"/> over its length — a metre in nine at midspan and
        /// nothing at all near a tower.</para>
        /// </summary>
        private static void AddHangers(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape deckShape,
            float fromDistance,
            float toDistance,
            float fromOffset,
            float toOffset,
            float fromRise,
            float toRise,
            float sag,
            float sign,
            List<float> supports)
        {
            float span = toDistance - fromDistance;
            int count = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(span) / HangerSpacing));

            for (int i = 1; i < count; i++)
            {
                float t = i / (float)count;

                float offset = Mathf.Lerp(fromOffset, toOffset, t);
                float rise = Mathf.Lerp(fromRise, toRise, t) - 4f * sag * t * (1f - t);

                float distance = fromDistance + span * t;
                Sample(path, deckShape, distance, out Vector3 centre, out Vector3 right, out Vector3 up);

                Vector3 top = centre + right * (offset * sign) + up * rise;
                Vector3 foot = centre + right * (deckShape.OuterHalfWidth * sign)
                               - up * deckShape.ShoulderDrop
                               + up * BridgeBuilder.ParapetHeight;

                if ((top - foot).magnitude < MinimumHanger)
                {
                    continue;
                }

                AddTube(buffer, SteelSubmesh, new[] { top, foot }, HangerHalf);

                // A hanger is a support. The checker is asking whether the deck is held, not what by —
                // and between the towers a hanger every twenty-two metres is the only thing that holds
                // it, so a list of piers alone would report the main span as nine hundred metres of
                // carriageway over air.
                supports?.Add(distance);
            }
        }

        /// <summary>
        /// Lamp standards along the parapet, one side's worth.
        ///
        /// <para>Not decoration: without them the deck at night is a kilometre of unlit asphalt with a
        /// shape overhead, and the one place in the world where the driver has no verge, no hedge and no
        /// horizon to judge position against. Every real crossing of this size is lit for the same
        /// reason. They stand on the parapet rather than beside it, because there is no verge to stand
        /// on.</para>
        /// </summary>
        private static void AddDeckLamps(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            in RoadFeature feature,
            float sign)
        {
            int count = Mathf.Max(1, Mathf.RoundToInt(feature.Length / LampSpacing));

            for (int i = 1; i < count; i++)
            {
                float distance = feature.StartDistance + feature.Length * i / count;

                Sample(path, roadShape, distance, out Vector3 centre, out Vector3 right, out Vector3 up);
                Vector3 along = Vector3.Cross(up, right).normalized;

                Vector3 seat = centre
                               + right * (roadShape.OuterHalfWidth * sign)
                               - up * roadShape.ShoulderDrop
                               + up * BridgeBuilder.ParapetHeight;

                // The column is steel and dark; only the head is on the bright slot, or a lamp post is a
                // strip light standing on the parapet.
                AddBox(buffer, SteelSubmesh, seat, right, along, up, 0.16f, LampHeight, 0.16f);
                AddBox(buffer, LampSubmesh, seat + up * LampHeight, right, along, up, 0.42f, 0.45f, 0.7f);
            }
        }

        /// <summary>The lamp beads strung along a cable. See the class remarks about why they never dim.</summary>
        private static void AddBeads(VegetationMeshBuffer buffer, Vector3[] cable)
        {
            float walked = 0f;
            float next = BeadSpacing * 0.5f;

            for (int i = 0; i + 1 < cable.Length; i++)
            {
                Vector3 a = cable[i];
                Vector3 b = cable[i + 1];
                float length = (b - a).magnitude;

                while (next <= walked + length && length > 0.001f)
                {
                    Vector3 at = Vector3.Lerp(a, b, (next - walked) / length);

                    Vector3 along = (b - a).normalized;
                    Vector3 across = Vector3.Cross(Vector3.up, along);
                    across = across.sqrMagnitude < 0.001f ? Vector3.right : across.normalized;

                    AddBox(buffer, LampSubmesh, at - Vector3.up * (CableHalf + BeadHalf),
                        across, along, Vector3.up, BeadHalf, BeadHalf * 2f, BeadHalf);

                    next += BeadSpacing;
                }

                walked += length;
            }
        }

        /// <summary>
        /// A square-section tube swept through a polyline.
        ///
        /// <para>Square rather than round, and four faces rather than eight, because a cable is under a
        /// metre across against a span of nine hundred: at any distance the silhouette is all of it, and
        /// the extra rings are triangles spent on a shape nobody can resolve. The section is squared to
        /// world up rather than to the segment, so consecutive rings line up and the tube does not twist
        /// where the curve steepens.</para>
        /// </summary>
        private static void AddTube(
            VegetationMeshBuffer buffer, int submesh, Vector3[] points, float half)
        {
            for (int i = 0; i + 1 < points.Length; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[i + 1];
                Vector3 along = b - a;

                if (along.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                along.Normalize();

                Vector3 across = Vector3.Cross(Vector3.up, along);
                across = across.sqrMagnitude < 0.001f ? Vector3.right : across.normalized;

                Vector3 up = Vector3.Cross(along, across).normalized;

                Vector3 x = across * half;
                Vector3 y = up * half;

                Vector3[] corners = { -x - y, -x + y, x + y, x - y };

                for (int c = 0; c < 4; c++)
                {
                    Vector3 c0 = corners[c];
                    Vector3 c1 = corners[(c + 1) % 4];

                    buffer.AddQuadFacing(submesh,
                        a + c0, b + c0, b + c1, a + c1, (c0 + c1).normalized);
                }
            }
        }

        /// <summary>A closed box between two points, sectioned in the frame it is handed.</summary>
        private static void AddPrismBetween(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 foot,
            Vector3 top,
            Vector3 right,
            Vector3 along,
            float halfAcross,
            float halfAlong)
        {
            Vector3 a = right * halfAcross;
            Vector3 b = along * halfAlong;

            Vector3[] corners = { -a - b, -a + b, a + b, a - b };

            for (int i = 0; i < 4; i++)
            {
                Vector3 c0 = corners[i];
                Vector3 c1 = corners[(i + 1) % 4];

                buffer.AddQuadFacing(submesh,
                    foot + c0, top + c0, top + c1, foot + c1, (c0 + c1).normalized);
            }

            Vector3 lid = (top - foot).normalized;

            buffer.AddQuadFacing(submesh,
                top + corners[0], top + corners[1], top + corners[2], top + corners[3], lid);
        }

        /// <summary>A closed box seated on <paramref name="seat"/>, in the frame it is handed.</summary>
        private static void AddBox(
            VegetationMeshBuffer buffer,
            int submesh,
            Vector3 seat,
            Vector3 right,
            Vector3 along,
            Vector3 up,
            float halfAcross,
            float height,
            float halfAlong)
        {
            Vector3 a = right * halfAcross;
            Vector3 b = along * halfAlong;
            Vector3 lift = up * height;

            Vector3[] corners = { -a - b, -a + b, a + b, a - b };

            for (int i = 0; i < 4; i++)
            {
                Vector3 c0 = corners[i];
                Vector3 c1 = corners[(i + 1) % 4];

                buffer.AddQuadFacing(submesh,
                    seat + c0, seat + c0 + lift, seat + c1 + lift, seat + c1, (c0 + c1).normalized);
            }

            buffer.AddQuadFacing(submesh,
                seat + corners[0] + lift, seat + corners[1] + lift,
                seat + corners[2] + lift, seat + corners[3] + lift, up);

            buffer.AddQuadFacing(submesh,
                seat + corners[3], seat + corners[2], seat + corners[1], seat + corners[0], -up);
        }
    }
}
