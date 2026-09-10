using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Every road sign on one course, as one mesh.
    ///
    /// <para><b>The placement is <c>FuelStationBuilder.ResolveSign</c>'s, not a second opinion.</b> Foot
    /// off the road's own line rather than off a terrain sample, standoff measured from
    /// <c>RoadShape.OuterHalfWidth</c>, and the same three keep-outs — a bore, a span, a forecourt's
    /// open frontage. That method worked all of it out for one sign on ten stations; this puts several
    /// hundred on seventy-five kilometres and the rules did not need changing, which is the argument for
    /// having read them rather than written new ones.</para>
    ///
    /// <para><b>What is deliberately different is which way the walk runs.</b> The station's sign hunts
    /// <i>backwards</i> for a clear spot, because a warning that arrives late is no warning. Nothing
    /// here does: a place-name board marks where the houses start, a bake marks a corner that is already
    /// under the wheels, and a sign that shuffled up the road to find easier ground would be marking
    /// somewhere else. Where the spot is not clear these are dropped instead, and the count is
    /// reported.</para>
    ///
    /// <para>No collider, like the delineator posts and the station's own board — see
    /// <c>DelineatorPostBuilder</c> for the reasoning, and <c>ValidateSigns</c> for the check that
    /// exists because <c>ValidateDriveableCorridor</c> cannot find something with nothing to sweep
    /// against.</para>
    /// </summary>
    public static class RoadSignBuilder
    {
        /// <summary>How far out from the paved edge a board on its own post stands, metres.</summary>
        /// <remarks>
        /// The station's advance sign's number. Well clear of the delineator posts at 0.56, and clear of
        /// a guard rail, which is what stands on this line where the ground falls away.
        /// </remarks>
        private const float Standoff = 3.5f;

        /// <summary>
        /// And for a bake, which stands closer, metres.
        ///
        /// <para>Closer because of where they go. A bake belongs on the outside of a tight bend, and the
        /// outside of a 20 m hairpin is the one place in this world where the flat shelf
        /// <see cref="RoadSignMeshes"/> assumes underfoot is least reliable —
        /// <c>ValidateRoadSupport</c> reports two twenty-metre stretches of the pass with nothing under
        /// the outer verge. Staying near the asphalt is what keeps that from mattering.</para>
        /// </remarks>
        private const float BakeStandoff = 2.4f;

        /// <summary>Corner radius at or below which a bend gets bakes, metres.</summary>
        /// <remarks>
        /// 48 catches the pass's 20 m hairpins, the Weissjoch's 26–36 and the tighter of the Steilufer's
        /// 38–70, and leaves the Ebental — nothing under 150 — and the motorway alone. A bake on an open
        /// sweeper is a sign that means nothing, and a road where every corner is marked is a road where
        /// no corner is.
        /// </remarks>
        /// <remarks>
        /// Public for one caller: <c>WorldPreviewRenderer</c> walks a road looking for the corners this
        /// builder marked, so that the camera and the builder cannot disagree about which bend is a
        /// bend. It is the argument <c>Minimap.ForwardBias</c> and the gauges' <c>LayOutFace</c> already
        /// make — the alternative is a tool carrying its own copy of the number, which agrees until the
        /// first retune and then photographs bare verge while reporting nothing.
        /// </remarks>
        public const float BakeRadius = 48f;

        /// <summary>The window the radius is measured over, metres. The delineators' own.</summary>
        /// <remarks>Public beside <see cref="BakeRadius"/> and for the same caller.</remarks>
        public const float RadiusWindow = 10f;

        /// <summary>Spacing of bakes through a bend, metres.</summary>
        private const float BakeSpacing = 13f;

        /// <summary>How far up the road from a portal or a span its board stands, metres.</summary>
        private const float PortalWarning = 45f;

        /// <summary>Margins for the three keep-outs, metres. Bigger than the posts', as the sign is.</summary>
        private const float PortalClearance = 30f;
        private const float BridgeClearance = 10f;
        private const float ForecourtClearance = 45f;

        /// <summary>What one build put down, for the log and for the check.</summary>
        public readonly struct Tally
        {
            public readonly int PlaceNames;
            public readonly int Bakes;
            public readonly int Portals;

            /// <summary>Spots that were wanted and refused, for want of clear verge.</summary>
            public readonly int Dropped;

            public Tally(int placeNames, int bakes, int portals, int dropped)
            {
                PlaceNames = placeNames;
                Bakes = bakes;
                Portals = portals;
                Dropped = dropped;
            }

            public int Total => PlaceNames + Bakes + Portals;
        }

        /// <summary>
        /// Builds every sign on <paramref name="course"/>, or returns null where none was placed.
        ///
        /// <para>Null rather than an empty mesh, matching <c>GuardRailBuilder.Build</c> and
        /// <c>DelineatorPostBuilder.Build</c>, so the caller reports it the same way. A motorway
        /// carriageway with no town, no fork of its own and no corner under 48 m legitimately gets
        /// nothing.</para>
        /// </summary>
        public static Mesh Build(
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            string meshName,
            List<int> usedSubmeshes,
            out Tally tally,
            List<Vector3> posts = null,
            float nearSide = 0f)
        {
            tally = default;

            if (path == null || path.Length <= 0f || course == null)
            {
                return null;
            }

            var buffer = new VegetationMeshBuffer(RoadSignMeshes.SubmeshCount);

            int places = 0;
            int bakes = 0;
            int portals = 0;
            int dropped = 0;

            AddFeatureSigns(buffer, path, roadShape, course, posts, nearSide,
                ref places, ref portals, ref dropped);

            AddBakes(buffer, path, roadShape, course, posts, nearSide, ref bakes, ref dropped);

            tally = new Tally(places, bakes, portals, dropped);

            if (buffer.IsEmpty)
            {
                return null;
            }

            buffer.MergeTinted(RoadSignMeshes.Tints());

            return buffer.ToMesh(meshName, usedSubmeshes);
        }

        /// <summary>
        /// The three kinds that hang off something the course already knows about.
        ///
        /// <para>Read off <c>RoadCourse.Features</c> rather than worked out again, which is the rule this
        /// project states about checkers and applies just as well to builders: the course is where a
        /// village, a fork and a bore are recorded, and a second opinion about where they are would
        /// agree with it right up until one of them was wrong.</para>
        /// </summary>
        private static void AddFeatureSigns(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            List<Vector3> posts,
            float nearSide,
            ref int places,
            ref int portals,
            ref int dropped)
        {
            IReadOnlyList<RoadFeature> features = course.Features;

            for (int i = 0; i < features.Count; i++)
            {
                RoadFeature feature = features[i];

                switch (feature.Kind)
                {
                    case RoadFeatureKind.Village:
                        // One at each end and on opposite hands, because they are two signs for two
                        // directions of travel rather than one sign seen twice: a board announcing a
                        // village stands on the near side of the road for the driver arriving at it.
                        if (TryPlace(buffer, path, roadShape, course, RoadSignMeshes.Kind.PlaceName,
                                feature.StartDistance, 1f, Standoff, 1f, nearSide, posts, ref dropped))
                        {
                            places++;
                        }

                        if (TryPlace(buffer, path, roadShape, course, RoadSignMeshes.Kind.PlaceName,
                                feature.EndDistance, -1f, Standoff, 1f, nearSide, posts, ref dropped))
                        {
                            places++;
                        }

                        break;

                    // Bores only, and not the spans, because the pictogram is an arch and a viaduct is
                    // not one. A board that says "tunnel" 45 m before a bridge is a sign this world
                    // would be better off without — the same argument that took the direction sign out
                    // of this file, one kind along.
                    case RoadFeatureKind.Tunnel:
                    case RoadFeatureKind.Gallery:
                        if (TryPlace(buffer, path, roadShape, course, RoadSignMeshes.Kind.Portal,
                                feature.StartDistance - PortalWarning, 1f, Standoff, 1f, nearSide,
                                posts, ref dropped))
                        {
                            portals++;
                        }

                        if (TryPlace(buffer, path, roadShape, course, RoadSignMeshes.Kind.Portal,
                                feature.EndDistance + PortalWarning, -1f, Standoff, 1f, nearSide,
                                posts, ref dropped))
                        {
                            portals++;
                        }

                        break;
                }
            }
        }

        /// <summary>
        /// Bakes through every bend under <see cref="BakeRadius"/>.
        ///
        /// <para>Walked rather than read off the course, because a corner is the one thing here the
        /// course does not record — it is a property of the curve, and <c>GetRadiusAtDistance</c> is what
        /// every other builder asks. The delineator posts already tighten their spacing on the same
        /// measurement; this is the same question asked one threshold further in.</para>
        /// </summary>
        private static void AddBakes(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            List<Vector3> posts,
            float nearSide,
            ref int bakes,
            ref int dropped)
        {
            float length = path.Length;

            for (float at = 0f; at <= length; at += BakeSpacing)
            {
                if (path.GetRadiusAtDistance(at, RadiusWindow) > BakeRadius)
                {
                    continue;
                }

                float turn = TurnSign(path, at);
                if (turn == 0f)
                {
                    continue;
                }

                // The outside of the bend, which is where a bake goes and the only place it can be seen
                // from: on the inside it is behind the driver's own line through the corner.
                float side = -turn;

                // Pointing back across the road, at the centre of the curve — see AddPointer for why
                // that one direction serves traffic coming the other way as well.
                if (TryPlace(buffer, path, roadShape, course, RoadSignMeshes.Kind.Chevron,
                        at, side, BakeStandoff, -1f, nearSide, posts, ref dropped))
                {
                    bakes++;
                }
            }
        }

        /// <summary>
        /// Which way the road turns here: +1 left, −1 right, 0 where it is too straight to say.
        ///
        /// <para>From the headings either side rather than from a curvature, because the sign is all
        /// that is wanted and a cross product of two unit vectors gives it without a division that has
        /// to be guarded against a straight.</para>
        /// </summary>
        private static float TurnSign(IRoadPath path, float at)
        {
            Vector3 before = path.GetDirectionAtDistance(Mathf.Max(0f, at - RadiusWindow));
            Vector3 after = path.GetDirectionAtDistance(Mathf.Min(path.Length, at + RadiusWindow));

            float cross = before.z * after.x - before.x * after.z;

            return Mathf.Abs(cross) < 0.02f ? 0f : Mathf.Sign(cross);
        }

        /// <summary>
        /// Puts one sign down, or refuses and says so.
        ///
        /// <para>The three refusals are the station's: inside a bore, on a span, or across a forecourt's
        /// frontage. A sign in a bore is in the rock; one on a viaduct has no ground to stand in and
        /// would hang off the parapet; one on a forecourt stands in the way of the cars the forecourt
        /// exists for. A fourth — off the end of the road — falls out of the distance clamp.</para>
        /// </summary>
        private static bool TryPlace(
            VegetationMeshBuffer buffer,
            IRoadPath path,
            in RoadShape roadShape,
            RoadCourse course,
            RoadSignMeshes.Kind kind,
            float at,
            float side,
            float standoff,
            float hand,
            float nearSide,
            List<Vector3> posts,
            ref int dropped)
        {
            // A divided road has one verge, and it is not the one in the middle. Every side this method
            // is handed elsewhere is a reasoned choice — the nearside for the direction being warned,
            // the outside of a bend — and every one of them is wrong on a carriageway whose left hand is
            // the other carriageway. ValidateSigns caught four of these on the first build: motorway
            // portal boards standing 0.6 m inside the asphalt of the road alongside.
            if (nearSide != 0f)
            {
                side = nearSide;
            }

            if (at < 0f || at > path.Length)
            {
                dropped++;
                return false;
            }

            if (course.IsCoveredOrNear(at, PortalClearance)
                || course.IsBridged(at, BridgeClearance)
                || course.IsForecourt(at, ForecourtClearance))
            {
                dropped++;
                return false;
            }

            Vector3 on = path.GetPositionAtDistance(at);
            Vector3 forward = path.GetDirectionAtDistance(at);
            Vector3 outward = path.GetRightAtDistance(at) * side;

            Vector3 foot = on + outward * (roadShape.OuterHalfWidth + standoff);

            // Off the road's own line and never off the terrain, for the reason RoadSignMeshes.Bury
            // records: within the verge the field is a flat shelf at this height, and the post is sunk
            // far enough to swallow what it is not.
            foot.y = on.y - roadShape.ShoulderDrop;

            RoadSignMeshes.Add(buffer, kind, foot, forward, outward, hand);

            // Handed back so ValidateSigns can ask the one question this builder cannot: a post is
            // placed off *this* road's half-width, and nothing here knows that a hairpin's outside verge
            // is the carriageway of the leg below it.
            posts?.Add(foot);

            return true;
        }
    }
}
