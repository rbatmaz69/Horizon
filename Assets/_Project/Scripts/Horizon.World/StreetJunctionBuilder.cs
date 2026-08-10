using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Fills the middle of every junction: a paved pad where streets meet each other, and a bell-mouth
    /// throat where one meets the trunk road.
    ///
    /// <para>Nothing else in the project builds junctions. <see cref="RoadMeshBuilder"/> and
    /// <see cref="TownStreetBuilder"/> both emit ribbons and leave their ends as open edges — there is no
    /// cap, no skirt and nothing to attach to — so without this, streets stop near one another and leave
    /// a step you can see from the car.</para>
    ///
    /// <para><b>Two kinds of junction, and the rule is stated once here.</b> A node on the trunk road
    /// gets a throat, because the trunk road is a single continuous ribbon whose markings live in an
    /// atlas keyed on arc length: it cannot be trimmed back to make room for a pad without the dashes
    /// jumping. Every other node gets a pad, which is the better article — it has kerbs that turn the
    /// corner, and the streets meeting it are trimmed so the surfaces are one piece.</para>
    /// </summary>
    public static class StreetJunctionBuilder
    {
        /// <summary>
        /// The floor on |sin| between two streets' bearings when working out how far back to trim.
        ///
        /// Two streets meeting at a shallow angle need a far longer trim than two meeting square: the
        /// trim has to clear the *width* of the other street measured along this one, which is its
        /// half-width divided by the sine of the angle between them, and that runs away to infinity as
        /// the angle closes. The floor and the ceiling below stop a near-collinear pair from trimming
        /// twenty metres off a ninety-metre street.
        /// </summary>
        private const float MinimumSine = 0.35f;

        /// <summary>Longest trim, as a multiple of the street's own outer half-width.</summary>
        private const float MaximumTrimFactor = 2.5f;

        /// <summary>How far back from the trunk centreline a street's own ribbon starts, beyond the throat.</summary>
        private const float TrunkThroatReach = 11f;

        /// <summary>
        /// Ribbons start a little *before* their trim point, so pad and ribbon overlap rather than abut.
        ///
        /// Two coplanar mesh colliders that merely touch can drop a raycast wheel for a frame on the
        /// seam, which reads as the car catching on nothing at all.
        /// </summary>
        public const float RibbonOverlap = 0.25f;

        /// <summary>
        /// Works out how far back every street stops short of every junction, for the whole network, and
        /// before a single ribbon is built.
        ///
        /// The order matters: a trim is a property of a node, and the ribbons consume it. Building a
        /// ribbon first and trimming afterwards would mean rebuilding it.
        /// </summary>
        public static void ResolveTrims(StreetNetwork network, float trunkOuterHalfWidth)
        {
            for (int n = 0; n < network.Nodes.Count; n++)
            {
                StreetNode node = network.Nodes[n];

                for (int i = 0; i < node.Degree; i++)
                {
                    StreetEdge edge = network.Edges[node.Edges[i]];
                    float trim;

                    if (node.OnTrunkRoad)
                    {
                        trim = trunkOuterHalfWidth + TrunkThroatReach;
                    }
                    else
                    {
                        trim = edge.HalfOuter;

                        for (int j = 0; j < node.Degree; j++)
                        {
                            if (j == i)
                            {
                                continue;
                            }

                            float between = Mathf.Abs(Mathf.Sin(
                                (node.Bearings[i] - node.Bearings[j]) * Mathf.Deg2Rad));

                            StreetEdge other = network.Edges[node.Edges[j]];
                            trim = Mathf.Max(trim, other.HalfOuter / Mathf.Max(MinimumSine, between));
                        }

                        trim = Mathf.Min(trim, edge.HalfOuter * MaximumTrimFactor);
                    }

                    if (edge.FromNode == node.Index)
                    {
                        edge.TrimStart = trim;
                    }
                    else
                    {
                        edge.TrimEnd = trim;
                    }
                }
            }

            // A short street between two wide junctions can have its two trims meet in the middle, which
            // would leave a ribbon of negative length. Scaling both back keeps them proportional and
            // leaves the pads where they are.
            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];
                float wanted = edge.TrimStart + edge.TrimEnd;
                float available = edge.Length - 4f;

                if (wanted > available && wanted > 0.01f)
                {
                    float scale = Mathf.Max(0f, available) / wanted;
                    edge.TrimStart *= scale;
                    edge.TrimEnd *= scale;

                    Debug.LogWarning($"[Horizon] Street {i} is {edge.Length:0} m long and its junctions "
                                     + $"wanted {wanted:0.0} m of it. Trims scaled to {scale:0.00}; the "
                                     + "layout table is asking for a street shorter than the junctions "
                                     + "at its ends.");
                }
            }

            for (int n = 0; n < network.Nodes.Count; n++)
            {
                BuildPadOutlines(network, network.Nodes[n]);
            }
        }

        /// <summary>
        /// Walks a node's incident streets in bearing order and records the pad polygon on the node.
        ///
        /// <para>For each street, at its trim point, the corners of its own cross-section — left and
        /// right looking outward from the junction, on all three rings. Between one street's right corner
        /// and the next street's left corner goes a <b>fillet</b>: a short arc swung about the node.
        /// Without it every junction has a hard triangular notch where the two footways meet, and that
        /// notch is the whole difference between something that reads as a junction and two roads
        /// overlapping.</para>
        ///
        /// <para>A dead end falls out of the same walk with nothing special about it: with one incident
        /// street the fillet runs almost the whole way round, which is a turning head.</para>
        ///
        /// <para>The polygon is star-shaped about the node centre — which is what makes a fan
        /// triangulation valid — precisely because the trims were corrected for the angle between the
        /// streets. <c>ValidateStreetNetwork</c> checks that mechanically rather than taking it on
        /// trust.</para>
        /// </summary>
        private static void BuildPadOutlines(StreetNetwork network, StreetNode node)
        {
            node.PadGutter = null;
            node.PadKerbTop = null;
            node.PadOutline = null;
            node.PadKerbedAfter = null;

            if (node.OnTrunkRoad || node.Degree == 0)
            {
                return;
            }

            int degree = node.Degree;

            // Both corners of every street first, so a fillet knows where it has to land. Building them
            // as it goes is what made the first version's arcs miss the next corner by a metre.
            var outward = new Vector3[degree];
            var leftGutter = new Vector3[degree];
            var leftKerb = new Vector3[degree];
            var leftOuter = new Vector3[degree];
            var rightGutter = new Vector3[degree];
            var rightKerb = new Vector3[degree];
            var rightOuter = new Vector3[degree];

            for (int i = 0; i < degree; i++)
            {
                StreetEdge edge = network.Edges[node.Edges[i]];
                TownStreetShape shape = edge.Shape;

                bool atStart = edge.FromNode == node.Index;
                float at = atStart ? edge.TrimStart : edge.Length - edge.TrimEnd;

                // Looking outward from the junction, "left" is the path's own left at the start of an
                // edge and its right at the end of one.
                float sign = atStart ? -1f : 1f;

                float half = shape.HalfWidth * sign;
                float kerb = (shape.HalfWidth + shape.KerbFace) * sign;
                float outer = shape.HalfOuter * sign;
                float lift = shape.SurfaceLift;
                float top = lift + shape.KerbHeight;

                Vector3 direction = edge.Path.GetDirectionAtDistance(at) * (atStart ? 1f : -1f);
                direction.y = 0f;
                outward[i] = direction.normalized;

                leftGutter[i] = TownStreetBuilder.PointAcross(edge.Path, shape, at, half, lift);
                leftKerb[i] = TownStreetBuilder.PointAcross(edge.Path, shape, at, kerb, top);
                leftOuter[i] = TownStreetBuilder.PointAcross(edge.Path, shape, at, outer, top);

                rightGutter[i] = TownStreetBuilder.PointAcross(edge.Path, shape, at, -half, lift);
                rightKerb[i] = TownStreetBuilder.PointAcross(edge.Path, shape, at, -kerb, top);
                rightOuter[i] = TownStreetBuilder.PointAcross(edge.Path, shape, at, -outer, top);
            }

            var gutter = new List<Vector3>(degree * 6);
            var kerbTop = new List<Vector3>(degree * 6);
            var outerRing = new List<Vector3>(degree * 6);
            var kerbed = new List<bool>(degree * 6);

            Vector3 centre = node.Position;

            for (int i = 0; i < degree; i++)
            {
                int next = (i + 1) % degree;

                gutter.Add(leftGutter[i]);
                kerbTop.Add(leftKerb[i]);
                outerRing.Add(leftOuter[i]);

                // The span leaving the left corner crosses the mouth of this street, so it carries no
                // kerb; every other span runs between two streets and does.
                kerbed.Add(false);

                gutter.Add(rightGutter[i]);
                kerbTop.Add(rightKerb[i]);
                outerRing.Add(rightOuter[i]);
                kerbed.Add(true);

                // The gap the two streets leave between them decides how much corner there is to round.
                float gap = Mathf.DeltaAngle(node.Bearings[i], node.Bearings[next]);
                if (gap <= 0f)
                {
                    gap += 360f;
                }

                // One step count for all three rings — the kerb pass reads them as parallel arrays, and
                // deriving the count per ring from that ring's own geometry gave the outer ring a
                // different number of points from the gutter.
                int steps = Mathf.Clamp(Mathf.RoundToInt(gap / 45f), 1, 8);

                // A corner between two streets is rounded off at the nose; an opening this wide is not a
                // corner at all but the head of a dead end or the back of a very obtuse T, and wants
                // swinging round the node instead.
                bool head = gap > 150f;

                AddFillet(centre, rightGutter[i], leftGutter[next], outward[i], outward[next], steps,
                    head, gutter, kerbed);
                AddFillet(centre, rightKerb[i], leftKerb[next], outward[i], outward[next], steps,
                    head, kerbTop, null);
                AddFillet(centre, rightOuter[i], leftOuter[next], outward[i], outward[next], steps,
                    head, outerRing, null);
            }

            node.PadGutter = gutter.ToArray();
            node.PadKerbTop = kerbTop.ToArray();
            node.PadOutline = outerRing.ToArray();
            node.PadKerbedAfter = kerbed.ToArray();
        }

        /// <summary>
        /// The corner between two streets, as a quadratic through the point where their kerb lines would
        /// have met.
        ///
        /// <para>Swinging the corner round the node centre instead — which is what this did first — turns
        /// every junction into a disc as wide as the trims are long. From above the town read as a chain
        /// of roundabouts, which is a striking way to discover that a corner radius and a junction radius
        /// are different things. A real corner is square with its nose rounded off, and the nose is where
        /// the two kerb lines cross.</para>
        ///
        /// <para>Near-parallel streets have no such crossing — the lines meet somewhere out past the edge
        /// of the town — so those fall back to a straight span, which is the right answer for them
        /// anyway.</para>
        /// </summary>
        private static void AddFillet(
            Vector3 centre,
            Vector3 from,
            Vector3 to,
            Vector3 outFrom,
            Vector3 outTo,
            int steps,
            bool head,
            List<Vector3> into,
            List<bool> kerbed)
        {
            if (head)
            {
                AddTurningHead(centre, from, to, steps, into, kerbed);
                return;
            }

            Vector3 corner = KerbLineCrossing(from, outFrom, to, outTo);

            for (int step = 1; step < steps; step++)
            {
                float t = step / (float)steps;
                float inverse = 1f - t;

                into.Add(inverse * inverse * from + 2f * inverse * t * corner + t * t * to);
                kerbed?.Add(true);
            }
        }

        /// <summary>
        /// The far end of a dead end: an arc swung about the node, which is exactly the shape a car turns
        /// round in. The same swing applied to an ordinary corner is what made every junction in the town
        /// render as a disc.
        /// </summary>
        private static void AddTurningHead(
            Vector3 centre, Vector3 from, Vector3 to, int steps, List<Vector3> into, List<bool> kerbed)
        {
            Vector3 a = from - centre;
            Vector3 b = to - centre;
            a.y = 0f;
            b.y = 0f;

            float radiusA = a.magnitude;
            float radiusB = b.magnitude;
            float angleA = Mathf.Atan2(a.x, a.z) * Mathf.Rad2Deg;
            float angleB = Mathf.Atan2(b.x, b.z) * Mathf.Rad2Deg;

            float sweep = Mathf.DeltaAngle(angleA, angleB);
            if (sweep <= 0f)
            {
                sweep += 360f;
            }

            for (int step = 1; step < steps; step++)
            {
                float t = step / (float)steps;
                float angle = (angleA + sweep * t) * Mathf.Deg2Rad;
                float radius = Mathf.Lerp(radiusA, radiusB, t);

                into.Add(new Vector3(
                    centre.x + Mathf.Sin(angle) * radius,
                    Mathf.Lerp(from.y, to.y, t),
                    centre.z + Mathf.Cos(angle) * radius));

                kerbed?.Add(true);
            }
        }

        /// <summary>
        /// Where the two streets' kerb lines cross, in plan. Falls back to the midpoint where they are
        /// too near parallel for the crossing to be anywhere useful.
        /// </summary>
        private static Vector3 KerbLineCrossing(
            Vector3 from, Vector3 outFrom, Vector3 to, Vector3 outTo)
        {
            float cross = outFrom.x * outTo.z - outFrom.z * outTo.x;
            if (Mathf.Abs(cross) < 0.15f)
            {
                return (from + to) * 0.5f;
            }

            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float s = (dx * outTo.z - dz * outTo.x) / cross;

            // Clamped, because the crossing of two near-parallel lines runs away fast and a control point
            // fifty metres out of the junction bows the kerb across the carriageway.
            s = Mathf.Clamp(s, -30f, 30f);

            Vector3 corner = from + outFrom * s;
            corner.y = (from.y + to.y) * 0.5f;
            return corner;
        }

        /// <summary>
        /// Emits one node's pad: the carriageway as a fan from the centre, then a kerb face and footway
        /// strip around every span that is not a street mouth.
        /// </summary>
        public static void AppendPad(StreetNetwork network, int nodeIndex, VegetationMeshBuffer into)
        {
            StreetNode node = network.Nodes[nodeIndex];
            if (node.PadGutter == null || node.PadGutter.Length < 3)
            {
                return;
            }

            int count = node.PadGutter.Length;
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                sum += node.PadGutter[i].y;
            }

            // The hub takes the mean height of the gutter ring rather than the ground beneath it, so the
            // pad is flush with every street that meets it however the floor rolls underneath.
            var hub = new Vector3(node.Position.x, sum / count, node.Position.z);

            // The outline is walked in ascending bearing — clockwise seen from above — and in Unity's
            // left-handed frame a clockwise ring fanned from its centre in walk order is the one that
            // comes out facing up. Up is the hint for the kerbs too, not the direction they face: a kerb
            // here is 0.25 m of run against 0.14 m of rise, so it is a steep ramp whose normal is mostly
            // vertical, and a horizontal hint would be deciding the winding on the smaller component.
            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % count;

                // A span with no length has no direction either, so the normal derived from it is noise
                // and the flip counter reports it as a fault. It contributes nothing to the mesh: two
                // corners a centimetre apart make a triangle with no area.
                if ((node.PadGutter[next] - node.PadGutter[i]).sqrMagnitude < 0.0004f)
                {
                    continue;
                }

                into.AddTriangleFacing(
                    TownStreetBuilder.SurfaceSubmesh,
                    hub, node.PadGutter[i], node.PadGutter[next], Vector3.up);

                if (!node.PadKerbedAfter[i])
                {
                    continue;
                }

                // Wound the other way round from the fan above, and that is not an inconsistency: the
                // fan runs from the hub outwards while these run along the ring, so the two orders that
                // give an upward normal are opposite. Measured, not reasoned about — the flip counter
                // settled it in two runs.
                into.AddQuadFacing(
                    TownStreetBuilder.KerbSubmesh,
                    node.PadGutter[next], node.PadGutter[i], node.PadKerbTop[i], node.PadKerbTop[next],
                    Vector3.up);

                into.AddQuadFacing(
                    TownStreetBuilder.FootwaySubmesh,
                    node.PadKerbTop[next], node.PadKerbTop[i], node.PadOutline[i], node.PadOutline[next],
                    Vector3.up);
            }
        }

        /// <summary>
        /// A throat where a street leaves the trunk road: a quad strip stretched from the trunk's outer
        /// edge to the street's own cross-section at its trim point, so the surface is continuous from
        /// one carriageway into the other.
        ///
        /// Deliberately not a *marked* junction. The trunk road's markings live in a baked atlas whose v
        /// coordinate is arc length in its own frame, so two paths share no dash phase and there is no
        /// unpainted column to fall back on. Painting a junction is a texture problem, not a geometry
        /// one, and it is not this method's business.
        /// </summary>
        public static void AppendTrunkMouth(
            StreetNetwork network,
            int nodeIndex,
            IRoadPath trunk,
            in RoadShape trunkShape,
            float alongTrunk,
            VegetationMeshBuffer into)
        {
            StreetNode node = network.Nodes[nodeIndex];
            if (node.Degree != 1 || trunk == null)
            {
                return;
            }

            StreetEdge edge = network.Edges[node.Edges[0]];
            bool atStart = edge.FromNode == nodeIndex;
            float handover = atStart ? edge.TrimStart : edge.Length - edge.TrimEnd;

            const int steps = 8;
            float throat = TrunkThroatReach;

            // Which side of the trunk road the mouth opens onto comes from the street itself, not from
            // which side the town is on. Most of the streets leave towards the valley floor and two
            // climb the other way, and taking the town's side for all five put those two mouths on the
            // far side of the trunk road from the street they were supposed to join.
            Vector3 trunkRight = trunk.GetRightAtDistance(alongTrunk);
            Vector3 outward = edge.Path.GetPositionAtDistance(handover) - node.Position;
            outward.y = 0f;

            float side = Mathf.Sign(Vector3.Dot(outward, trunkRight));

            // Which of the street's two edges pairs with which end of the mouth is *measured*, not
            // reasoned about from the side the town is on and which end of the edge the junction is.
            // Both of those sign conventions are right until the day a street leaves the other way round,
            // and the failure is a strip folded through itself in the middle of a junction — an hourglass
            // with a hole where the two carriageways were supposed to meet.
            Vector3 trunkForward = trunk.GetDirectionAtDistance(alongTrunk);
            float positive = Vector3.Dot(
                StreetEdgePoint(edge, handover, edge.HalfOuter) - node.Position, trunkForward);

            float first = positive < 0f ? edge.HalfOuter : -edge.HalfOuter;

            // The strip is walked backwards on one side of the trunk road, because a mirrored strip is
            // wound the other way round. The mesh would come out right either way — the buffer corrects
            // it — but a builder that authors half its faces backwards is a builder that has stopped
            // being able to tell anyone when something is wrong.
            for (int i = 0; i < steps; i++)
            {
                float t0 = Sweep(side, i / (float)steps);
                float t1 = Sweep(side, (i + 1) / (float)steps);

                Vector3 innerA = TrunkEdge(trunk, alongTrunk, trunkShape, side, t0, throat);
                Vector3 innerB = TrunkEdge(trunk, alongTrunk, trunkShape, side, t1, throat);
                Vector3 outerA = StreetEdgePoint(edge, handover, Mathf.Lerp(first, -first, t0));
                Vector3 outerB = StreetEdgePoint(edge, handover, Mathf.Lerp(first, -first, t1));

                into.AddQuadFacing(
                    TownStreetBuilder.SurfaceSubmesh, innerA, innerB, outerB, outerA, Vector3.up);
            }
        }

        /// <summary>Runs a sweep parameter backwards on the left-hand side, so both mirrors wind alike.</summary>
        private static float Sweep(float side, float t)
        {
            return side < 0f ? 1f - t : t;
        }

        /// <summary>
        /// A point on the trunk road's outer edge, swept from one side of the mouth to the other.
        ///
        /// Sampled from the path rather than interpolated between two corners, so the mouth follows the
        /// road where it curves instead of cutting the corner off.
        /// </summary>
        private static Vector3 TrunkEdge(
            IRoadPath trunk, float alongTrunk, in RoadShape shape, float side, float t, float throat)
        {
            float along = Mathf.Clamp(alongTrunk + Mathf.Lerp(-throat, throat, t), 0f, trunk.Length);

            Vector3 centre = trunk.GetPositionAtDistance(along);
            Vector3 right = trunk.GetRightAtDistance(along);

            return centre + right * (shape.OuterHalfWidth * side)
                   + Vector3.up * (shape.SurfaceLift - shape.ShoulderDrop);
        }

        /// <summary>A point across the street's cross-section, where the throat hands over to the ribbon.</summary>
        private static Vector3 StreetEdgePoint(StreetEdge edge, float at, float across)
        {
            return TownStreetBuilder.PointAcross(
                edge.Path, edge.Shape, at, across, edge.Shape.SurfaceLift);
        }
    }
}
