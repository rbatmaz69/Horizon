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
        private const float DeckStep = 8f;

        /// <summary>Depth of the girder below the carriageway, metres.</summary>
        private const float GirderDepth = 2.2f;

        /// <summary>How far the girder is drawn in from the edge of the shoulder, each side.</summary>
        private const float GirderInset = 1.2f;

        private const float PierHalfWidth = 1.6f;
        private const float PierHalfDepth = 1.0f;

        /// <summary>How far a pier is sunk below the ground it lands on, so it never floats on a slope.</summary>
        private const float FootBurial = 1.5f;

        /// <summary>
        /// A pier shorter than this is not worth building — the deck is nearly on the ground, which is
        /// the abutment rather than the span.
        /// </summary>
        private const float MinimumPier = 2.5f;

        private const float ParapetHeight = 1.1f;
        private const float ParapetThickness = 0.35f;

        /// <summary>
        /// Builds every bridge on a course as one mesh, or returns null if the course has none.
        ///
        /// <para>One mesh for all of them, and not chunked, for the reason <see cref="GuardRailBuilder"/>
        /// gives: a few thousand triangles is cheaper as one draw call than as streaming bookkeeping.
        /// Worth revisiting if a road ever has a dozen viaducts on it.</para>
        /// </summary>
        /// <param name="path">The carriageway being carried — one per structure, so a divided road builds twice.</param>
        /// <param name="course">Read for its <see cref="RoadFeatureKind.Bridge"/> spans, in centreline distances.</param>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            RoadCourse course,
            List<int> usedSubmeshes,
            string meshName = "BridgeMesh")
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
                    AddSpan(buffer, path, roadShape, field, feature);
                }
            }

            return buffer.IsEmpty ? null : buffer.ToMesh(meshName, usedSubmeshes);
        }

        private static void AddSpan(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            MountainField field,
            in RoadFeature feature)
        {
            float span = feature.Length;
            int steps = Mathf.Max(2, Mathf.CeilToInt(span / DeckStep) + 1);

            var centres = new Vector3[steps];
            var rights = new Vector3[steps];
            var ups = new Vector3[steps];

            for (int step = 0; step < steps; step++)
            {
                float distance = feature.StartDistance + span * step / (steps - 1);

                centres[step] = path.GetPositionAtDistance(distance);
                rights[step] = path.GetBankedRightAtDistance(
                    distance, roadShape.MaxBankDegrees, roadShape.FullBankRadius);

                Vector3 up = Vector3.Cross(path.GetDirectionAtDistance(distance), rights[step]).normalized;
                ups[step] = up.y < 0f ? -up : up;
            }

            AddGirder(buffer, roadShape, centres, rights, ups);
            AddParapets(buffer, roadShape, centres, rights, ups);
            AddPiers(buffer, roadShape, field, centres, rights, ups, span);
        }

        /// <summary>
        /// The box girder: a closed tube swept along the deck, drawn in from the shoulder edge so the
        /// carriageway visibly overhangs it. That overhang is most of what makes a viaduct read as one
        /// from below rather than as a slab on sticks.
        /// </summary>
        private static void AddGirder(
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

        private static void AddParapets(
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
        /// Pier pairs down to the terrain, skipping any that would be too short to be a pier.
        ///
        /// <para>Each leg is measured against <see cref="MountainField.HeightAt"/> at its own foot rather
        /// than at the deck centre. On a valley side those two differ by metres, and a pair sized from
        /// the centre leaves the downhill leg hanging.</para>
        /// </summary>
        private static void AddPiers(
            VegetationMeshBuffer buffer,
            in RoadShape roadShape,
            MountainField field,
            Vector3[] centres,
            Vector3[] rights,
            Vector3[] ups,
            float span)
        {
            int bays = Mathf.Max(1, Mathf.RoundToInt(span / PierSpacing));
            float legOffset = (roadShape.OuterHalfWidth - GirderInset) * 0.55f;

            // Interior supports only: the ends of the span are the abutments, where the deck meets solid
            // ground and a pier would be standing in the hillside it is already resting on.
            for (int bay = 1; bay < bays; bay++)
            {
                float t = bay / (float)bays;
                int step = Mathf.Clamp(Mathf.RoundToInt(t * (centres.Length - 1)), 0, centres.Length - 1);

                Vector3 soffit = centres[step]
                                 - ups[step] * (roadShape.ShoulderDrop + GirderDepth);

                for (int leg = 0; leg < 2; leg++)
                {
                    float sign = leg == 0 ? -1f : 1f;
                    Vector3 top = soffit + rights[step] * (legOffset * sign);

                    float ground = field.HeightAt(top.x, top.z) - FootBurial;
                    if (top.y - ground < MinimumPier)
                    {
                        continue;
                    }

                    var foot = new Vector3(top.x, ground, top.z);
                    AddPier(buffer, foot, top, rights[step]);
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
