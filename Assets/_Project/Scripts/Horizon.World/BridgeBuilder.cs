using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Carries a carriageway across a valley: a girder under the deck, piers down to the ground, and a
    /// parapet along each edge.
    ///
    /// <para><b>The half that is not in this file.</b> A bridge is only a bridge because the ground
    /// underneath it is missing, and the ground is <see cref="MountainField"/>'s business: the shelf
    /// follows the nearest carriageway everywhere, so without
    /// <see cref="RoadFeatureKind.Bridge"/> telling it to leave a span alone, a road taken over a valley
    /// simply carries the valley floor up with it. What is built here stands in the hole that makes.
    /// Build a bridge without marking the feature and you get an elegant structure sitting on a solid
    /// embankment, which is the failure mode to recognise.</para>
    ///
    /// <para><b>Matches <see cref="RoadFeatureKind.Bridge"/> alone, never
    /// <see cref="RoadFeatureKind.Suspension"/>.</b> <see cref="RoadCourse.IsBridged"/> reports both,
    /// because everything else in the world wants them treated alike; the piers below are the one thing
    /// that does not, and a pier pair every forty metres across a shipping channel is what the other
    /// kind exists to avoid. <c>SuspensionBridgeBuilder</c> takes those spans.</para>
    ///
    /// <para>Measured first and built second, the same two-pass shape as
    /// <see cref="GuardRailBuilder"/>: every pier has to know how far it is falling before any geometry
    /// exists, because a pier is a different object at 4 m and at 40 m, and the deck it hangs from is
    /// the same either way.</para>
    /// </summary>
    public static class BridgeBuilder
    {
        /// <summary>Concrete: deck girder and piers.</summary>
        public const int ConcreteSubmesh = 0;

        /// <summary>The parapet along each edge, which is what you see from the car.</summary>
        public const int ParapetSubmesh = 1;

        private const int SubmeshCount = 2;

        /// <summary>Spacing of the pier pairs, metres.</summary>
        private const float PierSpacing = 40f;

        /// <summary>Cross-sections along the deck. Finer than the piers, so the soffit follows the road.</summary>
        internal const float DeckStep = 8f;

        /// <summary>Depth of the girder below the carriageway, metres.</summary>
        internal const float GirderDepth = 2.2f;

        /// <summary>How far the girder is drawn in from the edge of the shoulder, each side.</summary>
        private const float GirderInset = 1.5f;

        // Scaled with the deck they carry when the roads were widened for the cars. None of these
        // three fails if it is left behind — a pier is still a pier — but a viaduct twenty per cent
        // wider on legs of the old thickness reads as a deck that has outgrown its supports.
        private const float PierHalfWidth = 2f;
        private const float PierHalfDepth = 1.25f;

        /// <summary>How far a pier is sunk below the ground it lands on, so it never floats on a slope.</summary>
        private const float FootBurial = 1.5f;

        /// <summary>
        /// A pier shorter than this is not worth building — the deck is nearly on the ground, which is
        /// the abutment rather than the span.
        /// </summary>
        private const float MinimumPier = 2.5f;

        /// <summary>
        /// Height of the parapet above the shoulder line, metres.
        ///
        /// <para>Public because it is the top of the thing a hanger lands on, and
        /// <c>ValidateSuspensionBridges</c> — which lives in the editor assembly — has to know whether
        /// the cable clears it.</para>
        /// </summary>
        public const float ParapetHeight = 1.1f;

        // The thickness went up with the deck; the height did not. A parapet's height is measured
        // against the car behind it, and the cars grew 15 % in height against 25 % in plan — near
        // enough that 1.1 m is still a parapet.
        internal const float ParapetThickness = 0.44f;

        /// <summary>
        /// Builds every bridge on a course as one mesh, or returns null if the course has none.
        ///
        /// <para>One mesh for all of them, and not chunked, for the reason <see cref="GuardRailBuilder"/>
        /// gives: a few thousand triangles is cheaper as one draw call than as streaming bookkeeping.
        /// Worth revisiting if a road ever has a dozen viaducts on it.</para>
        /// </summary>
        /// <param name="path">The carriageway being carried — one per structure, so a divided road builds twice.</param>
        /// <param name="course">Read for its <see cref="RoadFeatureKind.Bridge"/> spans, in centreline distances.</param>
        /// <param name="supports">
        /// Optional. Every support this actually built, as a distance along the course.
        ///
        /// <para>Filled by the builder rather than recomputed by the checker, because a check that
        /// works out for itself where the piers ought to be is a second opinion, and the two agree
        /// right up until the moment one of them is wrong. <c>ValidateBridgeSupport</c> asks this list
        /// how long the longest unsupported stretch of deck is, and a pier the builder skipped is a
        /// pier that is missing from it.</para>
        /// </param>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            List<int> usedSubmeshes,
            string meshName = "BridgeMesh",
            List<float> supports = null)
        {
            if (course == null)
            {
                return null;
            }

            var buffer = new VegetationMeshBuffer(SubmeshCount);

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind == RoadFeatureKind.Bridge && feature.Length > 1f)
                {
                    AddSpan(buffer, path, roadShape, field, feature, supports);
                }
            }

            return buffer.IsEmpty ? null : buffer.ToMesh(meshName, usedSubmeshes);
        }

        private static void AddSpan(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            in RoadFeature feature,
            List<float> supports)
        {
            float span = feature.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(span / DeckStep) + 1);

            var centres = new Vector3[steps];
            var rights = new Vector3[steps];
            var ups = new Vector3[steps];

            for (int step = 0; step < steps; step++)
            {
                float distance = feature.StartDistance + span * step / (steps - 1);
                Sample(path, roadShape, distance, out centres[step], out rights[step], out ups[step]);
            }

            AddGirder(buffer, roadShape, centres, rights, ups);
            AddParapets(buffer, roadShape, centres, rights, ups);
            AddPiers(buffer, path, roadShape, field, feature.StartDistance, feature.EndDistance, supports);

            // The abutments. Not piers — they are where the deck meets the ground it was leaving — but
            // they are supports, and the checker is asking where the deck is held up rather than what
            // the thing holding it is called.
            supports?.Add(feature.StartDistance);
            supports?.Add(feature.EndDistance);
        }

        /// <summary>
        /// The road's frame at a distance: centreline, banked right, and the up that follows from them.
        ///
        /// <para>Internal and shared with <see cref="SuspensionBridgeBuilder"/> for the reason
        /// <see cref="AddGirder"/> is: the deck of one kind of bridge and the deck of the other are the
        /// same deck, so they had better be sampled by the same three lines of code.</para>
        /// </summary>
        internal static void Sample(
            IRoadPath path,
            in RoadShape roadShape,
            float distance,
            out Vector3 centre,
            out Vector3 right,
            out Vector3 up)
        {
            centre = path.GetPositionAtDistance(distance);
            right = path.GetBankedRightAtDistance(
                distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

            Vector3 raised = Vector3.Cross(path.GetDirectionAtDistance(distance), right).normalized;
            up = raised.y < 0f ? -raised : raised;
        }

        /// <summary>
        /// The box girder: a closed tube swept along the deck, drawn in from the shoulder edge so the
        /// carriageway visibly overhangs it.
        ///
        /// <para><b>Internal rather than private, and shared with
        /// <see cref="SuspensionBridgeBuilder"/> rather than copied into it.</b> A stiffening girder and
        /// a viaduct girder are the same object — a deck is a deck, whatever is holding it up — and the
        /// half of a bridge that differs between the two kinds is entirely below or above it. The same
        /// goes for the parapet, which is the part a driver actually looks at and which has no business
        /// being two slightly different heights depending on the structure under it.</para> That overhang is most of what makes a viaduct read as one
        /// from below rather than as a slab on sticks.
        /// </summary>
        internal static void AddGirder(
            VegetationMeshBuffer buffer,
            in RoadShape roadShape,
            Vector3[] centres,
            Vector3[] rights,
            Vector3[] ups)
        {
            float half = roadShape.OuterHalfWidth - GirderInset;

            for (int step = 0; step + 1 < centres.Length; step++)
            {
                // The soffit sits a girder's depth under the shoulder line, not under the crown, so the
                // camber does not tilt the underside of the bridge with it.
                Vector3 topLeftA = centres[step] - rights[step] * half - ups[step] * roadShape.ShoulderDrop;
                Vector3 topRightA = centres[step] + rights[step] * half - ups[step] * roadShape.ShoulderDrop;
                Vector3 topLeftB = centres[step + 1] - rights[step + 1] * half - ups[step + 1] * roadShape.ShoulderDrop;
                Vector3 topRightB = centres[step + 1] + rights[step + 1] * half - ups[step + 1] * roadShape.ShoulderDrop;

                Vector3 dropA = ups[step] * GirderDepth;
                Vector3 dropB = ups[step + 1] * GirderDepth;

                Vector3 bottomLeftA = topLeftA - dropA;
                Vector3 bottomRightA = topRightA - dropA;
                Vector3 bottomLeftB = topLeftB - dropB;
                Vector3 bottomRightB = topRightB - dropB;

                // Soffit, seen from below by anything driving under it.
                buffer.AddQuadFacing(ConcreteSubmesh,
                    bottomLeftA, bottomLeftB, bottomRightB, bottomRightA, -ups[step]);

                buffer.AddQuadFacing(ConcreteSubmesh,
                    topLeftA, bottomLeftA, bottomLeftB, topLeftB, -rights[step]);

                buffer.AddQuadFacing(ConcreteSubmesh,
                    topRightA, bottomRightA, bottomRightB, topRightB, rights[step]);
            }

            CapGirder(buffer, roadShape, centres, rights, ups, 0, -1);
            CapGirder(buffer, roadShape, centres, rights, ups, centres.Length - 1, 1);
        }

        /// <summary>Closes the girder at an abutment, so the deck does not read as a hollow tube.</summary>
        private static void CapGirder(
            VegetationMeshBuffer buffer,
            in RoadShape roadShape,
            Vector3[] centres,
            Vector3[] rights,
            Vector3[] ups,
            int step,
            float facing)
        {
            float half = roadShape.OuterHalfWidth - GirderInset;

            Vector3 top = centres[step] - ups[step] * roadShape.ShoulderDrop;
            Vector3 across = rights[step] * half;
            Vector3 drop = ups[step] * GirderDepth;

            Vector3 outward = Vector3.Cross(rights[step], ups[step]).normalized * facing;

            buffer.AddQuadFacing(ConcreteSubmesh,
                top - across, top + across, top + across - drop, top - across - drop, outward);
        }

        internal static void AddParapets(
            VegetationMeshBuffer buffer,
            in RoadShape roadShape,
            Vector3[] centres,
            Vector3[] rights,
            Vector3[] ups)
        {
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;
                float inner = roadShape.OuterHalfWidth * sign;
                float outer = (roadShape.OuterHalfWidth + ParapetThickness) * sign;

                for (int step = 0; step + 1 < centres.Length; step++)
                {
                    Vector3 baseA = centres[step] - ups[step] * roadShape.ShoulderDrop;
                    Vector3 baseB = centres[step + 1] - ups[step + 1] * roadShape.ShoulderDrop;

                    Vector3 innerBottomA = baseA + rights[step] * inner;
                    Vector3 outerBottomA = baseA + rights[step] * outer;
                    Vector3 innerBottomB = baseB + rights[step + 1] * inner;
                    Vector3 outerBottomB = baseB + rights[step + 1] * outer;

                    Vector3 liftA = ups[step] * ParapetHeight;
                    Vector3 liftB = ups[step + 1] * ParapetHeight;

                    // Inner face, which is the one a driver sees.
                    buffer.AddQuadFacing(ParapetSubmesh,
                        innerBottomA, innerBottomA + liftA, innerBottomB + liftB, innerBottomB,
                        rights[step] * -sign);

                    buffer.AddQuadFacing(ParapetSubmesh,
                        outerBottomA, outerBottomA + liftA, outerBottomB + liftB, outerBottomB,
                        rights[step] * sign);

                    buffer.AddQuadFacing(ParapetSubmesh,
                        innerBottomA + liftA, outerBottomA + liftA, outerBottomB + liftB, innerBottomB + liftB,
                        ups[step]);
                }
            }
        }

        /// <summary>
        /// The deck slab outboard of the carriageway, where the deck is wider than the road on it.
        ///
        /// <para>Needed the moment anything widens the deck, because <see cref="AddGirder"/> builds a
        /// soffit and two flanks and no top: on an ordinary viaduct the road ribbon <i>is</i> the top,
        /// and it reaches exactly as far as the parapet. Move the parapet outwards without laying this
        /// and the gap between the asphalt edge and the rail is a hole through the bridge.</para>
        ///
        /// <para>Level with the parapet's own base rather than following the camber out, which is what a
        /// footway on a real deck does — the crossfall is for water on the carriageway and stops at the
        /// kerb.</para>
        /// </summary>
        internal static void AddFootways(
            VegetationMeshBuffer buffer,
            float inner,
            float outer,
            in RoadShape deckShape,
            Vector3[] centres,
            Vector3[] rights,
            Vector3[] ups)
        {
            if (outer - inner <= 0.01f)
            {
                return;
            }

            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;

                for (int step = 0; step + 1 < centres.Length; step++)
                {
                    Vector3 baseA = centres[step] - ups[step] * deckShape.ShoulderDrop;
                    Vector3 baseB = centres[step + 1] - ups[step + 1] * deckShape.ShoulderDrop;

                    Vector3 innerA = baseA + rights[step] * (inner * sign);
                    Vector3 outerA = baseA + rights[step] * (outer * sign);
                    Vector3 innerB = baseB + rights[step + 1] * (inner * sign);
                    Vector3 outerB = baseB + rights[step + 1] * (outer * sign);

                    buffer.AddQuadFacing(ConcreteSubmesh,
                        innerA, outerA, outerB, innerB, ups[step]);
                }
            }
        }

        /// <summary>
        /// Spacing of the collision band's cross-sections, metres.
        ///
        /// <para>Three times <see cref="DeckStep"/>. What is being approximated is a straight wall, and
        /// the error a longer chord makes on the tightest curve any bridge here sits on is a couple of
        /// centimetres — against a saving of two thirds of the triangles PhysX has to cook and keep
        /// resident for the length of every bridge and every guard rail in the world.</para>
        /// </summary>
        internal const float BarrierStep = 24f;

        /// <summary>
        /// The barrier a car actually hits: a plain band along a line, with no posts in it.
        ///
        /// <para><b>Deliberately not the visible geometry.</b> A <c>MeshCollider</c> taken from the rail
        /// as drawn is a row of re-entrant corners four metres apart, and a car sliding along it catches
        /// on every one of them — which is the objection the old "no collider at all" comment was
        /// really making. A smooth wall in the same place answers it: the car is held and slides off,
        /// which is what a barrier is for, and the posts stay a picture.</para>
        ///
        /// <para>Inner face, top and outer face — three quads a segment. No end caps and no soffit: a
        /// non-convex mesh collider generates contacts from either side of a triangle, so the wall does
        /// not have to be a closed volume to be solid, and the two faces it does have are the two the
        /// car can reach.</para>
        /// </summary>
        internal static void AddBarrier(
            VegetationMeshBuffer buffer,
            Vector3[] bases,
            Vector3[] rights,
            Vector3[] ups,
            float inner,
            float outer,
            float height)
        {
            for (int side = 0; side < 2; side++)
            {
                float sign = side == 0 ? -1f : 1f;

                for (int step = 0; step + 1 < bases.Length; step++)
                {
                    Vector3 innerBottomA = bases[step] + rights[step] * (inner * sign);
                    Vector3 outerBottomA = bases[step] + rights[step] * (outer * sign);
                    Vector3 innerBottomB = bases[step + 1] + rights[step + 1] * (inner * sign);
                    Vector3 outerBottomB = bases[step + 1] + rights[step + 1] * (outer * sign);

                    Vector3 liftA = ups[step] * height;
                    Vector3 liftB = ups[step + 1] * height;

                    buffer.AddQuadFacing(ConcreteSubmesh,
                        innerBottomA, innerBottomA + liftA, innerBottomB + liftB, innerBottomB,
                        rights[step] * -sign);

                    buffer.AddQuadFacing(ConcreteSubmesh,
                        innerBottomA + liftA, outerBottomA + liftA, outerBottomB + liftB,
                        innerBottomB + liftB, ups[step]);

                    buffer.AddQuadFacing(ConcreteSubmesh,
                        outerBottomA, outerBottomA + liftA, outerBottomB + liftB, outerBottomB,
                        rights[step] * sign);
                }
            }
        }

        /// <summary>
        /// Samples a stretch of course at <see cref="BarrierStep"/> and hands back the parapet's base
        /// line, for the collision band to be swept along.
        /// </summary>
        internal static void SampleBarrierLine(
            IRoadPath path,
            in RoadShape deckShape,
            float startDistance,
            float endDistance,
            out Vector3[] bases,
            out Vector3[] rights,
            out Vector3[] ups)
        {
            float span = Mathf.Max(0.01f, endDistance - startDistance);
            int steps = Mathf.Max(2, Mathf.CeilToInt(span / BarrierStep) + 1);

            bases = new Vector3[steps];
            rights = new Vector3[steps];
            ups = new Vector3[steps];

            for (int step = 0; step < steps; step++)
            {
                float distance = startDistance + span * step / (steps - 1);
                Sample(path, deckShape, distance, out Vector3 centre, out rights[step], out ups[step]);
                bases[step] = centre - ups[step] * deckShape.ShoulderDrop;
            }
        }

        /// <summary>
        /// The collision wall along every viaduct parapet on a course, or null if the course has none.
        ///
        /// <para>A mesh of its own rather than a submesh of the bridge: what is drawn and what is solid
        /// are different questions here, and <c>PrototypeSetup.CreateMeshObject</c> has taken a separate
        /// <c>collisionMesh</c> since the tunnels needed exactly this distinction.</para>
        /// </summary>
        public static Mesh BuildParapetCollision(
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            string meshName = "BridgeParapetCollisionMesh")
        {
            if (course == null)
            {
                return null;
            }

            var buffer = new VegetationMeshBuffer(1);

            for (int i = 0; i < course.Features.Count; i++)
            {
                RoadFeature feature = course.Features[i];
                if (feature.Kind != RoadFeatureKind.Bridge || feature.Length <= 1f)
                {
                    continue;
                }

                SampleBarrierLine(path, roadShape, feature.StartDistance, feature.EndDistance,
                    out Vector3[] bases, out Vector3[] rights, out Vector3[] ups);

                AddBarrier(buffer, bases, rights, ups,
                    roadShape.OuterHalfWidth, roadShape.OuterHalfWidth + ParapetThickness,
                    ParapetHeight);
            }

            return buffer.IsEmpty ? null : buffer.ToMesh(meshName, new List<int>(1));
        }

        /// <summary>
        /// How deep the water may be under a pier foot before the pier is left out, metres.
        ///
        /// <para>A pier on a shallow bank is a pier; a pier in the navigable channel is the thing a
        /// suspension span exists in order not to build. The test is against the depth rather than
        /// against wet-or-dry because the side spans of a crossing come down over the shore, and
        /// refusing to stand in six inches of water would leave them hanging over dry-ish ground for
        /// the sake of a rule about the middle of the strait.</para>
        /// </summary>
        private const float NavigableDepth = 6f;

        /// <summary>
        /// Pier pairs down to the terrain, skipping any that would be too short to be a pier or would
        /// stand in the shipping channel.
        ///
        /// <para>Each leg is measured against <see cref="MountainField.HeightAt"/> at its own foot rather
        /// than at the deck centre. On a valley side those two differ by metres, and a pair sized from
        /// the centre leaves the downhill leg hanging.</para>
        ///
        /// <para><b>Takes a stretch of course rather than a deck that has already been sampled</b>, and
        /// is internal, so <see cref="SuspensionBridgeBuilder"/> can put piers under its side spans —
        /// which are a hundred and fifty metres of deck each and were, until this took a range, held up
        /// by nothing whatever. A pier is a pier; the two kinds of bridge differ in what happens between
        /// the towers, not in what happens outside them.</para>
        ///
        /// <para><b>At least two bays, never one.</b> <c>Max(1, span / 40)</c> and a loop over the
        /// interior means every span under about sixty metres emitted no pier at all — a deck over nine
        /// metres of carved-out air, standing on nothing, and no check in the build looked. The three
        /// viaducts authored so far are all long enough to have hidden it.</para>
        /// </summary>
        internal static void AddPiers(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            float startDistance,
            float endDistance,
            List<float> supports = null)
        {
            float span = endDistance - startDistance;
            if (span <= 1f)
            {
                return;
            }

            int bays = Mathf.Max(2, Mathf.RoundToInt(span / PierSpacing));
            float legOffset = (roadShape.OuterHalfWidth - GirderInset) * 0.55f;

            // Interior supports only: the ends of the span are the abutments, where the deck meets solid
            // ground and a pier would be standing in the hillside it is already resting on.
            for (int bay = 1; bay < bays; bay++)
            {
                float distance = startDistance + span * bay / bays;
                Sample(path, roadShape, distance, out Vector3 centre, out Vector3 right, out Vector3 up);

                Vector3 soffit = centre - up * (roadShape.ShoulderDrop + GirderDepth);
                bool stands = false;

                for (int leg = 0; leg < 2; leg++)
                {
                    float sign = leg == 0 ? -1f : 1f;
                    Vector3 top = soffit + right * (legOffset * sign);

                    float ground = field.HeightAt(top.x, top.z) - FootBurial;
                    if (top.y - ground < MinimumPier)
                    {
                        continue;
                    }

                    if (field.IsUnderWater(top.x, top.z, ground + NavigableDepth))
                    {
                        continue;
                    }

                    var foot = new Vector3(top.x, ground, top.z);
                    AddPier(buffer, foot, top, right);
                    stands = true;
                }

                if (stands)
                {
                    supports?.Add(distance);
                }
            }
        }

        private static void AddPier(VegetationMeshBuffer buffer, Vector3 foot, Vector3 top, Vector3 right)
        {
            // A pier is upright regardless of the deck's camber — it is standing on the ground, not
            // hanging off the road — so its section is squared to world up rather than to the bridge.
            Vector3 across = right;
            across.y = 0f;
            across = across.sqrMagnitude < 0.001f ? Vector3.right : across.normalized;

            Vector3 along = Vector3.Cross(Vector3.up, across).normalized;

            Vector3 a = across * PierHalfWidth;
            Vector3 b = along * PierHalfDepth;

            Vector3[] corners = { -a - b, -a + b, a + b, a - b };

            for (int i = 0; i < 4; i++)
            {
                Vector3 c0 = corners[i];
                Vector3 c1 = corners[(i + 1) % 4];

                Vector3 outward = (c0 + c1).normalized;

                buffer.AddQuadFacing(ConcreteSubmesh,
                    foot + c0, top + c0, top + c1, foot + c1, outward);
            }
        }
    }
}
