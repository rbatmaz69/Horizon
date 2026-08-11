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

        /// <summary>
        /// Longest trim, as a multiple of the street's own outer half-width.
        ///
        /// Public because it is also how far a junction pad can reach out from its node, and that is what
        /// decides how much levelled ground a layout needs — see <see cref="TownNetworkSpec.MeasureExtent"/>.
        /// </summary>
        public const float MaximumTrimFactor = 2.5f;

        /// <summary>
        /// How much further back than the bare minimum a street is trimmed so its outer corner clears its
        /// neighbour's with room to spare. See the corner term in <see cref="ResolveTrims"/>.
        /// </summary>
        private const float CornerMargin = 1.3f;

        /// <summary>
        /// Radius of the kerb returns where a street meets the trunk road, metres.
        ///
        /// <para>It sets two things at once, which is why it is one number: how far back the street's own
        /// ribbon starts (<see cref="RoadShape.OuterHalfWidth"/> plus this), and how far along the trunk
        /// road the mouth opens either side of the junction (the street's paved half-width plus this).
        /// Those are the two halves of the same corner and they have to agree, or the return arc is not
        /// tangent to anything at one of its ends.</para>
        ///
        /// <para>Seven metres is a country-road turning: a car can take it at walking pace without
        /// clipping the kerb, and it is small enough that the mouth still reads as a turning off a road
        /// rather than as a fork in one.</para>
        /// </summary>
        public const float TrunkKerbReturn = 7f;

        /// <summary>Points along the trunk road's own edge across the mouth. Enough to follow a bend.</summary>
        private const int TrunkEdgeSteps = 6;

        /// <summary>Points round each kerb return.</summary>
        private const int ReturnSteps = 5;

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
                        trim = trunkOuterHalfWidth + TrunkKerbReturn;
                    }
                    else
                    {
                        trim = edge.HalfOuter;
                        float nearestGap = 180f;

                        for (int j = 0; j < node.Degree; j++)
                        {
                            if (j == i)
                            {
                                continue;
                            }

                            float gap = Mathf.Abs(Mathf.DeltaAngle(node.Bearings[i], node.Bearings[j]));
                            nearestGap = Mathf.Min(nearestGap, gap);

                            float between = Mathf.Abs(Mathf.Sin(gap * Mathf.Deg2Rad));

                            StreetEdge other = network.Edges[node.Edges[j]];
                            trim = Mathf.Max(trim, other.HalfOuter / Mathf.Max(MinimumSine, between));
                        }

                        // And the street's *own* width has to fit inside the gap to its nearest
                        // neighbour. The rule above only clears the other street's width measured along
                        // this one, which is a different and weaker condition — it says nothing about
                        // where this street's outer corners land.
                        //
                        // A corner sits atan(HalfOuter / trim) off the street's own axis, so keeping it
                        // out of the neighbour's half of the gap needs trim > HalfOuter / tan(gap / 2).
                        // Without this a wide street meeting a narrow one at fifty degrees puts its
                        // corner past the neighbour's, the pad outline goes backwards in bearing, and the
                        // fan folds a wedge through itself — which is exactly what the star-shape
                        // validator reports and what six pads round the new market square were doing.
                        //
                        // CornerMargin is not decoration. At exactly the limit the two corners land on
                        // the *same* bearing and the fillet between them spans nothing, so any difference
                        // in the two streets' widths or trims tips it over — which is why a square's
                        // right-angled corners were still folding after the term above was added, and
                        // only those. The margin also gives the corner something to round off.
                        float halfGap = Mathf.Max(1f, nearestGap * 0.5f) * Mathf.Deg2Rad;
                        trim = Mathf.Max(trim, edge.HalfOuter * CornerMargin / Mathf.Tan(halfGap));

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
                // At least three, so a corner has two points in it rather than none.
                //
                // <b>Rounding to a 45° step was giving tight corners one step, and one step means
                // AddFillet contributes no interior points at all</b> — the "rounded" corner was a bare
                // chord from one street's corner to the next's. That is fine on the gutter, which is why
                // the carriageway fan never complained, and wrong on the strips above it: the two ends
                // carry their own streets' cross-section offsets, which at Talheim's 62° junction point
                // 118° apart, so the kerb and footway between them twisted through most of a half-turn
                // and inverted.
                //
                // Two interior points rather than one for a second reason, which is height. A kerb is
                // 0.16 m tall on a high street and 0.10 on an alley, so a corner between them has 6 cm
                // of riser to get through; concentrated into one short segment that segment is mostly
                // *vertical*, and the normal of a footway face built across it is mostly horizontal —
                // at which point testing it against up decides nothing. Spread over two, no segment is
                // steep enough for that. Costs about a hundred and seventy triangles across the town.
                int steps = Mathf.Clamp(Mathf.RoundToInt(gap / 45f), 3, 8);

                // A corner between two streets is rounded off at the nose; an opening this wide is not a
                // corner at all but the head of a dead end or the back of a very obtuse T, and wants
                // swinging round the node instead.
                bool head = gap > 150f;

                AddNestedFillet(
                    centre,
                    rightGutter[i], rightKerb[i], rightOuter[i],
                    leftGutter[next], leftKerb[next], leftOuter[next],
                    outward[i], outward[next], steps, head,
                    gutter, kerbTop, outerRing, kerbed);
            }

            CollapseCoincident(gutter, kerbTop, outerRing, kerbed);

            node.PadGutter = gutter.ToArray();
            node.PadKerbTop = kerbTop.ToArray();
            node.PadOutline = outerRing.ToArray();
            node.PadKerbedAfter = kerbed.ToArray();
        }

        /// <summary>
        /// Drops ring points that advance the pad by less than the width of its own kerb.
        ///
        /// <para>A fillet whose two ends are nearly on top of each other emits a face a few centimetres
        /// long and a metre and a half deep, and the normal of a triangle that thin is decided by
        /// rounding rather than by geometry — which is how the last of the town's backwards faces
        /// survived two rounds of fixing the things that were actually crooked. Skipping the face would
        /// have left a notch; dropping the point removes the sliver and closes the ring over it.</para>
        ///
        /// <para>Five centimetres is measured against the pad's own finest feature, the 0.25 m kerb face:
        /// a step a fifth of that describes nothing the pad has. The seam from the last point back to the
        /// first is left alone, so the ring cannot be rotated out from under the caller.</para>
        /// </summary>
        private static void CollapseCoincident(
            List<Vector3> gutter, List<Vector3> kerbTop, List<Vector3> outerRing, List<bool> kerbed)
        {
            const float minimumStep = 0.05f;
            const float minimumSquared = minimumStep * minimumStep;

            for (int i = gutter.Count - 1; i >= 1 && gutter.Count > 3; i--)
            {
                if ((gutter[i] - gutter[i - 1]).sqrMagnitude >= minimumSquared
                    || (outerRing[i] - outerRing[i - 1]).sqrMagnitude >= minimumSquared)
                {
                    continue;
                }

                // The two spans either side of the dropped point become one, and it carries a kerb if
                // either did — so a kerb runs on round the corner instead of stopping at the seam.
                kerbed[i - 1] = kerbed[i - 1] || kerbed[i];

                gutter.RemoveAt(i);
                kerbTop.RemoveAt(i);
                outerRing.RemoveAt(i);
                kerbed.RemoveAt(i);
            }
        }

        /// <summary>
        /// One corner, on all three rings at once, as a single arc and two offsets of it.
        ///
        /// <para><b>The three rings were three separate curves, and that was the bug.</b> Each ran its own
        /// <see cref="AddFillet"/> — same tangents, but its own endpoints, and therefore its own solved
        /// corner point out of <see cref="KerbLineCrossing"/>. Three curves built that way are only
        /// roughly parallel, and at a tight corner they stop being parallel at all: the outer arc crosses
        /// inside the inner one, and the kerb face or footway strip between them is inside out. Seven
        /// faces across five junctions were doing exactly that, every one of them on a fillet at a node
        /// carrying three or four streets, and their normals were full sized rather than the slivers a
        /// degenerate span would give — which is what finally said the geometry was folded rather than
        /// merely thin.</para>
        ///
        /// <para>So the gutter is filleted and the other two rings are that arc plus an offset, lerped
        /// from the cross-section offset of the street the corner leaves to the one it arrives at. The
        /// endpoints land exactly on both streets' own corners as before, the rings cannot cross because
        /// they are now offsets of one curve, and the kerb narrows through a tight corner instead of
        /// turning through it — which is what a real kerb return does.</para>
        /// </summary>
        private static void AddNestedFillet(
            Vector3 centre,
            Vector3 fromGutter,
            Vector3 fromKerb,
            Vector3 fromOuter,
            Vector3 toGutter,
            Vector3 toKerb,
            Vector3 toOuter,
            Vector3 outFrom,
            Vector3 outTo,
            int steps,
            bool head,
            List<Vector3> gutter,
            List<Vector3> kerbTop,
            List<Vector3> outerRing,
            List<bool> kerbed)
        {
            int before = gutter.Count;
            AddFillet(centre, fromGutter, toGutter, outFrom, outTo, steps, head, gutter, kerbed);

            Vector3 kerbAtFrom = fromKerb - fromGutter;
            Vector3 kerbAtTo = toKerb - toGutter;
            Vector3 outerAtFrom = fromOuter - fromGutter;
            Vector3 outerAtTo = toOuter - toGutter;

            int added = gutter.Count - before;

            for (int k = 0; k < added; k++)
            {
                // The same t the fillet placed its k-th interior point at, so the offsets line up with the
                // arc rather than merely with each other.
                float t = (k + 1) / (float)(added + 1);
                Vector3 on = gutter[before + k];

                kerbTop.Add(on + Vector3.Lerp(kerbAtFrom, kerbAtTo, t));
                outerRing.Add(on + Vector3.Lerp(outerAtFrom, outerAtTo, t));
            }
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
        public static void AppendPad(
            StreetNetwork network,
            int nodeIndex,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            StreetNode node = network.Nodes[nodeIndex];
            if (node.PadGutter == null || node.PadGutter.Length < 3)
            {
                return;
            }

            int count = node.PadGutter.Length;

            // The hub takes the mean height of the gutter ring rather than the ground beneath it, so the
            // pad is flush with every street that meets it however the floor rolls underneath.
            var hub = new Vector3(node.Position.x, MeanHeight(node.PadGutter), node.Position.z);

            // The outline is walked in ascending bearing — clockwise seen from above — and in Unity's
            // left-handed frame a clockwise ring fanned from its centre in walk order is the one that
            // comes out facing up. Up is the hint for the kerbs too, not the direction they face: a kerb
            // here is 0.25 m of run against 0.14 m of rise, so it is a steep ramp whose normal is mostly
            // vertical, and a horizontal hint would be deciding the winding on the smaller component.
            //
            // A fan is right here and is *not* right for a trunk mouth, which is long and shallow rather
            // than roughly equiaxed — see AppendMouthSurface for what that costs and why it is a strip.
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

                // Each strip is guarded on its own ring, not on the gutter's.
                //
                // The three rings are parallel but not similar: they are filleted at different radii from
                // the node, so a corner tight enough to close the kerb-top ring to nothing still leaves a
                // gutter span with real length. Guarding all three on the gutter — which is what the fan
                // above does, correctly, for itself — let seven slivers through with normals that were
                // pure noise, and the flip counter reported every one of them. Three were kerb faces and
                // four were footways, which is what named them.
                //
                // Wound the other way round from the fan, and that is not an inconsistency: the fan runs
                // from the hub outwards while these run along the ring, so the two orders that give an
                // upward normal are opposite.
                if ((node.PadKerbTop[next] - node.PadKerbTop[i]).sqrMagnitude >= 0.0004f)
                {
                    into.AddQuadFacing(
                        TownStreetBuilder.KerbSubmesh,
                        node.PadGutter[next], node.PadGutter[i],
                        node.PadKerbTop[i], node.PadKerbTop[next],
                        Vector3.up);
                }

                if ((node.PadOutline[next] - node.PadOutline[i]).sqrMagnitude >= 0.0004f)
                {
                    into.AddQuadFacing(
                        TownStreetBuilder.FootwaySubmesh,
                        node.PadKerbTop[next], node.PadKerbTop[i],
                        node.PadOutline[i], node.PadOutline[next],
                        Vector3.up);
                }
            }

            AppendPadVerge(node, hub, field, terrainShape, into);
        }

        /// <summary>
        /// A grass skirt from the outer edge of the pad down onto the terrain, for the same reason the
        /// ribbons have one: a junction that stands proud of the ground beside it is a plateau, and a
        /// plateau is something you can drive off but not back onto.
        /// </summary>
        private static void AppendPadVerge(
            StreetNode node,
            Vector3 hub,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            if (field == null)
            {
                return;
            }

            const float width = 1.6f;
            int count = node.PadOutline.Length;
            var skirt = new Vector3[count];

            for (int i = 0; i < count; i++)
            {
                Vector3 radial = node.PadOutline[i] - hub;
                radial.y = 0f;

                Vector3 at = node.PadOutline[i] + radial.normalized * width;
                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                    out Vector3 ground, out Vector3 _);

                skirt[i] = new Vector3(at.x, ground.y + 0.02f, at.z);
            }

            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % count;

                if ((node.PadOutline[next] - node.PadOutline[i]).sqrMagnitude < 0.0004f)
                {
                    continue;
                }

                // Not across the mouth of a street. Grass goes behind a pavement, and a mouth has no
                // pavement — it has a carriageway, with the ribbon's own verge already running down
                // either side of it.
                //
                // This used to skirt every span, which laid a strip of grass clean across the entrance
                // to every street in the town: eighteen metres from one corner of a high street to the
                // other, with two corners at pad height and two on the terrain a quarter of a metre
                // below. A quad that twisted cannot agree with itself about which way it faces, and
                // seven of them were being turned round at build time — the flip counter naming a real
                // fault, as designed.
                if (!node.PadKerbedAfter[i])
                {
                    continue;
                }

                into.AddQuadFacing(
                    TownStreetBuilder.VergeSubmesh,
                    node.PadOutline[next], node.PadOutline[i], skirt[i], skirt[next],
                    Vector3.up);
            }
        }

        /// <summary>
        /// Paves a square: one fan from its centre out to the boundary, in the footway material.
        ///
        /// <para>Exactly the operation <see cref="AppendPad"/> performs on a junction, which is why it
        /// lives here rather than in a builder of its own — a square is a very large junction with nothing
        /// driving across the middle of it. The only differences are that the whole surface is footway
        /// rather than carriageway, and that there are no kerbs, because the kerb that matters is the one
        /// already standing along each of the streets around it.</para>
        ///
        /// <para>The fan holds because the boundary is star-shaped about its centroid, which a ring of
        /// four-to-six streets is. The layout table caps a square at six nodes for that reason.</para>
        /// </summary>
        public static void AppendSquare(TownSquare square, VegetationMeshBuffer into)
        {
            if (square?.Interior == null || square.Interior.Length < 3)
            {
                return;
            }

            Vector3[] ring = square.Interior;
            var hub = new Vector3(square.Centre.x, MeanHeight(ring), square.Centre.z);

            for (int i = 0; i < ring.Length; i++)
            {
                int next = (i + 1) % ring.Length;

                if ((ring[next] - ring[i]).sqrMagnitude < 0.0004f)
                {
                    continue;
                }

                into.AddTriangleFacing(
                    TownStreetBuilder.FootwaySubmesh, hub, ring[i], ring[next], Vector3.up);
            }
        }

        private static float MeanHeight(IReadOnlyList<Vector3> points)
        {
            float sum = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                sum += points[i].y;
            }

            return points.Count > 0 ? sum / points.Count : 0f;
        }

        /// <summary>
        /// A bell-mouth where a street leaves the trunk road: the two carriageways joined by a paved
        /// throat with a kerb return turning each corner, and the street's footway carried round both.
        ///
        /// <para>What this replaced is worth recording, because it looked finished and was not. The first
        /// version stretched a single quad strip from the trunk road's <i>outer shoulder</i> edge to the
        /// street's cross-section — one submesh, no kerbs, no footway, no grass. Three things came out of
        /// that. The paving read as a triangular apron dropped between two roads rather than as a
        /// junction, because nothing turned the corner. The pavement simply stopped at the trim point,
        /// eleven metres short of the road, so every street in Talheim ended in two floating kerb ends.
        /// And starting the surface at the outer edge of the shoulder put a half-metre dip in the
        /// driving line: you left the asphalt, crossed the gravel down to the bottom of the shoulder
        /// drop, and climbed back out of it onto the street.</para>
        ///
        /// <para>So the throat now starts at the <b>carriageway</b> edge and falls from there to the
        /// street's own surface — one continuous grade of about four per cent instead of a dip — and the
        /// corners are the same construction the town's own junctions use: an arc through the point where
        /// the two kerb lines cross, with all three rings following it. Where the return meets the trunk
        /// the kerb and footway taper to nothing, because the trunk road has no kerb for them to continue
        /// into and inventing one would put a raised edge along a road the player drives at speed.</para>
        ///
        /// <para>Deliberately still not a *marked* junction. The trunk road's markings live in a baked
        /// atlas whose v coordinate is arc length in its own frame, so two paths share no dash phase and
        /// there is no unpainted column to fall back on. Painting a junction is a texture problem, not a
        /// geometry one, and it is not this method's business.</para>
        /// </summary>
        public static void AppendTrunkMouth(
            StreetNetwork network,
            int nodeIndex,
            IRoadPath trunk,
            in RoadShape trunkShape,
            float alongTrunk,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            StreetNode node = network.Nodes[nodeIndex];
            if (node.Degree != 1 || trunk == null)
            {
                return;
            }

            StreetEdge edge = network.Edges[node.Edges[0]];
            TownStreetShape shape = edge.Shape;

            bool atStart = edge.FromNode == nodeIndex;
            float handover = atStart ? edge.TrimStart : edge.Length - edge.TrimEnd;

            // Which side of the trunk road the mouth opens onto comes from the street itself, not from
            // which side the town is on. Most of the streets leave towards the valley floor and two
            // climb the other way, and taking the town's side for all five put those two mouths on the
            // far side of the trunk road from the street they were supposed to join.
            Vector3 trunkRight = trunk.GetRightAtDistance(alongTrunk);
            Vector3 outward = edge.Path.GetPositionAtDistance(handover) - node.Position;
            outward.y = 0f;

            float side = Mathf.Sign(Vector3.Dot(outward, trunkRight));

            // The direction the street leaves the junction on, which is one of the two tangents every
            // return arc is built from.
            Vector3 streetOut = edge.Path.GetDirectionAtDistance(handover) * (atStart ? 1f : -1f);
            streetOut.y = 0f;
            streetOut = streetOut.normalized;

            Vector3 trunkForward = trunk.GetDirectionAtDistance(alongTrunk);
            trunkForward.y = 0f;
            trunkForward = trunkForward.normalized;

            // Which of the street's two edges pairs with which end of the mouth is *measured*, not
            // reasoned about from the side the town is on and which end of the edge the junction is.
            // Both of those sign conventions are right until the day a street leaves the other way round,
            // and the failure is a strip folded through itself in the middle of a junction — an hourglass
            // with a hole where the two carriageways were supposed to meet.
            float probe = Vector3.Dot(
                StreetPoint(edge, handover, shape.HalfOuter, shape.SurfaceLift) - node.Position,
                trunkForward);

            float aheadSign = probe > 0f ? 1f : -1f;

            // The mouth opens a kerb return either side of the street's own paved width. That is what
            // makes the return tangent to both roads: the same radius decides the trim in ResolveTrims.
            float mouthHalf = shape.HalfOuter + TrunkKerbReturn;
            float back = Mathf.Clamp(alongTrunk - mouthHalf, 0f, trunk.Length);
            float fore = Mathf.Clamp(alongTrunk + mouthHalf, 0f, trunk.Length);

            // --- The street side of the mouth, walked from the back end to the fore end: a kerb return
            // up onto the street, across the mouth of the street, and a kerb return back down.
            var gutter = new List<Vector3>(24);
            var kerbTop = new List<Vector3>(24);
            var outerRing = new List<Vector3>(24);
            var kerbed = new List<bool>(24);

            Vector3 backTrunk = TrunkEdge(trunk, trunkShape, back, side);
            Vector3 foreTrunk = TrunkEdge(trunk, trunkShape, fore, side);

            // Both ends of the chain sit on the trunk road's own edge, with all three rings on one point:
            // that is what tapers the kerb to nothing and the footway to no width exactly where the trunk
            // road's shoulder takes over, because the trunk road has no kerb for them to continue into.
            AddChainPoint(backTrunk, backTrunk, backTrunk, gutter, kerbTop, outerRing, kerbed, true);

            Vector3 backGutter = StreetCorner(
                edge, handover, -aheadSign, out Vector3 backKerb, out Vector3 backOuter);
            Vector3 foreGutter = StreetCorner(
                edge, handover, aheadSign, out Vector3 foreKerb, out Vector3 foreOuter);

            AddReturn(
                backTrunk, backTrunk, backTrunk,
                backGutter, backKerb, backOuter,
                -trunkForward, streetOut,
                gutter, kerbTop, outerRing, kerbed);

            // The span across the mouth of the street carries no kerb — it is where the ribbon takes over,
            // and the ribbon brings its own.
            AddChainPoint(backGutter, backKerb, backOuter, gutter, kerbTop, outerRing, kerbed, false);
            AddChainPoint(foreGutter, foreKerb, foreOuter, gutter, kerbTop, outerRing, kerbed, true);

            AddReturn(
                foreGutter, foreKerb, foreOuter,
                foreTrunk, foreTrunk, foreTrunk,
                streetOut, trunkForward,
                gutter, kerbTop, outerRing, kerbed);

            AddChainPoint(foreTrunk, foreTrunk, foreTrunk, gutter, kerbTop, outerRing, kerbed, false);

            // The pavement starts where it has a width to have — see DropSpansWithoutFootway.
            DropSpansWithoutFootway(kerbTop, outerRing, kerbed);

            // --- The trunk side, sampled along the road so the mouth follows it where it curves.
            var trunkChain = new Vector3[TrunkEdgeSteps + 1];
            for (int i = 0; i < trunkChain.Length; i++)
            {
                trunkChain[i] = TrunkEdge(
                    trunk, trunkShape, Mathf.Lerp(back, fore, i / (float)TrunkEdgeSteps), side);
            }

            AppendMouthSurface(trunkChain, gutter, into);
            AppendMouthKerbs(trunkChain, gutter, kerbTop, outerRing, kerbed, into);
            AppendMouthVerge(gutter, kerbTop, outerRing, kerbed, field, terrainShape, into);
        }

        /// <summary>One point of the street-side chain, on all three rings, and what the span after it carries.</summary>
        private static void AddChainPoint(
            Vector3 gutterAt,
            Vector3 kerbAt,
            Vector3 outerAt,
            List<Vector3> gutter,
            List<Vector3> kerbTop,
            List<Vector3> outerRing,
            List<bool> kerbed,
            bool kerbedAfter)
        {
            gutter.Add(gutterAt);
            kerbTop.Add(kerbAt);
            outerRing.Add(outerAt);
            kerbed.Add(kerbedAfter);
        }

        /// <summary>
        /// The paved throat, lofted between the trunk road's edge and the street side of the mouth.
        ///
        /// <para><b>A strip, not a fan, and that is the correction this method exists to record.</b> The
        /// pads are fanned from their node and it works because a junction pad is roughly as wide as it
        /// is deep. A trunk mouth is not: it is twenty-nine metres along the road against eight and a
        /// half deep, and a fan needs its ring to be star-shaped about the hub — which a long shallow
        /// trapezoid is not, at any hub. Near the corners the ring doubles back on itself while the hub
        /// stays far away along the road, so those triangles subtend almost no angle and their facing
        /// comes down to rounding.</para>
        ///
        /// <para>The first and last quads collapse to triangles, because the street chain begins and ends
        /// on the trunk chain's own endpoints. That is the taper, and it is correct: the buffer drops the
        /// half with no area.</para>
        /// </summary>
        private static void AppendMouthSurface(
            Vector3[] trunkChain, List<Vector3> streetChain, VegetationMeshBuffer into)
        {
            int steps = trunkChain.Length - 1;

            var street = new Vector3[trunkChain.Length];
            var up = new Vector3[trunkChain.Length];

            for (int i = 0; i < street.Length; i++)
            {
                street[i] = AlongChain(streetChain, i / (float)steps);
                up[i] = Vector3.up;
            }

            AppendStrip(TownStreetBuilder.SurfaceSubmesh, trunkChain, street, up, null, into);
        }

        /// <summary>
        /// The kerb face and footway along the street side of a mouth.
        ///
        /// The kerb takes its outward direction from the carriageway it edges rather than from
        /// <c>Vector3.up</c> — the hint <see cref="TownStreetBuilder.AppendStreet"/> uses for the same
        /// strip. A kerb here is a quarter-metre of run against a sixth of rise, so up and
        /// toward-the-road are both mostly right, and the one that is exactly right is the one the strip
        /// actually faces.
        /// </summary>
        private static void AppendMouthKerbs(
            Vector3[] trunkChain,
            List<Vector3> gutter,
            List<Vector3> kerbTop,
            List<Vector3> outerRing,
            List<bool> kerbed,
            VegetationMeshBuffer into)
        {
            Vector3 carriageway = trunkChain[trunkChain.Length / 2];

            var towardsRoad = new Vector3[gutter.Count];
            var up = new Vector3[gutter.Count];

            for (int i = 0; i < gutter.Count; i++)
            {
                Vector3 outward = carriageway - gutter[i];
                outward.y = 0f;

                towardsRoad[i] = outward.sqrMagnitude > 0.0001f ? outward.normalized : Vector3.up;
                up[i] = Vector3.up;
            }

            AppendStrip(TownStreetBuilder.KerbSubmesh, gutter, kerbTop, towardsRoad, kerbed, into);
            AppendStrip(TownStreetBuilder.FootwaySubmesh, kerbTop, outerRing, up, kerbed, into);
        }

        /// <summary>
        /// The grass skirt behind a mouth's footway, running from the back of the pavement down onto the
        /// terrain — the same thing the ribbons and the pads have, and for the same reason: paving that
        /// stands proud of the ground beside it is a plateau, and a plateau is something you can drive off
        /// but not back onto.
        ///
        /// Only behind the pavement. Where there is no pavement — the span across the mouth of the street,
        /// and the tapers onto the trunk road — there is nothing for grass to go behind, and a strip laid
        /// there would be a hedge across the junction.
        /// </summary>
        private static void AppendMouthVerge(
            List<Vector3> gutter,
            List<Vector3> kerbTop,
            List<Vector3> outerRing,
            List<bool> kerbed,
            MountainField field,
            in TerrainShape terrainShape,
            VegetationMeshBuffer into)
        {
            if (field == null)
            {
                return;
            }

            const float width = 1.6f;

            var skirt = new Vector3[outerRing.Count];
            var up = new Vector3[outerRing.Count];

            for (int i = 0; i < outerRing.Count; i++)
            {
                up[i] = Vector3.up;

                // Outward is away from the carriageway, which on a chain is the direction the footway
                // already runs in — from the kerb to its outer edge.
                Vector3 radial = outerRing[i] - kerbTop[i];
                radial.y = 0f;

                if (radial.sqrMagnitude < 0.0001f)
                {
                    skirt[i] = outerRing[i];
                    continue;
                }

                Vector3 at = outerRing[i] + radial.normalized * width;
                TerrainTileBuilder.SampleSurface(field, terrainShape, at.x, at.z,
                    out Vector3 ground, out Vector3 _);

                skirt[i] = new Vector3(at.x, ground.y + 0.02f, at.z);
            }

            AppendStrip(TownStreetBuilder.VergeSubmesh, outerRing, skirt, up, kerbed, into);
        }

        /// <summary>
        /// A quad strip between two chains of equal length, wound to face <paramref name="outward"/>.
        ///
        /// <para><b>The winding is measured once, not assumed.</b> A junction pad can assume one: its ring
        /// is always walked in ascending bearing about its node, so a face that comes out backwards means
        /// something upstream is wrong and the flip counter is right to say so. A mouth has no such
        /// invariant — one on the left of the trunk road is the mirror of one on the right, and the chain
        /// runs the opposite way round for it. Emitting through the counting path and letting it correct
        /// them would report five junctions' worth of faces as faults every build, which is how a counter
        /// stops being read.</para>
        ///
        /// <para>So the first span with a real normal decides the order for the whole strip, and the rest
        /// follow it. Three attempts at reasoning the order out from the side the mouth opens onto got it
        /// wrong three times; the geometry knows, and asking it costs one cross product.</para>
        /// </summary>
        /// <param name="used">Which spans to emit, or null for all of them.</param>
        private static void AppendStrip(
            int submesh,
            IReadOnlyList<Vector3> near,
            IReadOnlyList<Vector3> far,
            IReadOnlyList<Vector3> outward,
            IReadOnlyList<bool> used,
            VegetationMeshBuffer into)
        {
            int spans = Mathf.Min(near.Count, far.Count) - 1;
            bool decided = false;
            bool flip = false;

            for (int i = 0; i < spans; i++)
            {
                if (used != null && !used[i])
                {
                    continue;
                }

                Vector3 a = near[i];
                Vector3 b = near[i + 1];
                Vector3 c = far[i + 1];
                Vector3 d = far[i];

                if (!decided)
                {
                    Vector3 normal = Vector3.Cross(b - a, c - a) + Vector3.Cross(a - d, c - d);
                    if (normal.sqrMagnitude < 0.000001f)
                    {
                        continue;
                    }

                    flip = Vector3.Dot(normal, outward[i]) < 0f;
                    decided = true;
                }

                if (flip)
                {
                    into.AddTriangleRaw(submesh, a, d, c);
                    into.AddTriangleRaw(submesh, a, c, b);
                }
                else
                {
                    into.AddTriangleRaw(submesh, a, b, c);
                    into.AddTriangleRaw(submesh, a, c, d);
                }
            }
        }

        /// <summary>A point a normalised distance along a polyline, by arc length.</summary>
        private static Vector3 AlongChain(List<Vector3> chain, float t)
        {
            if (chain.Count == 1)
            {
                return chain[0];
            }

            float total = 0f;
            for (int i = 0; i < chain.Count - 1; i++)
            {
                total += Vector3.Distance(chain[i], chain[i + 1]);
            }

            float wanted = Mathf.Clamp01(t) * total;
            float walked = 0f;

            for (int i = 0; i < chain.Count - 1; i++)
            {
                float span = Vector3.Distance(chain[i], chain[i + 1]);
                if (walked + span >= wanted || i == chain.Count - 2)
                {
                    float within = span > 0.0001f ? (wanted - walked) / span : 0f;
                    return Vector3.Lerp(chain[i], chain[i + 1], Mathf.Clamp01(within));
                }

                walked += span;
            }

            return chain[chain.Count - 1];
        }

        /// <summary>
        /// Clears the kerb flag on any span whose footway has no width at one of its ends.
        ///
        /// <para>A bell-mouth's pavement tapers to nothing where it meets the trunk road — all three rings
        /// converge onto one point there, because the trunk road has no kerb for them to continue into.
        /// The strip over that last span is therefore a sliver with two coincident corners, and it is
        /// exactly the shape <see cref="AppendRings"/> cannot wind: the surviving triangle stands nearly
        /// on edge, so testing it against <c>Vector3.up</c> is deciding its facing on rounding. Five
        /// mouths produced ten faces the buffer had to turn round, which is the flip counter doing its
        /// job — the geometry came out right and the code that wrote it was wrong.</para>
        ///
        /// <para>Dropping the span is the fix rather than a special normal, because the sliver should not
        /// be drawn at all. A pavement that converges to a mathematical point on the edge of a
        /// carriageway is not a thing; one that starts a metre along the kerb return is what a junction
        /// actually looks like.</para>
        /// </summary>
        private static void DropSpansWithoutFootway(
            List<Vector3> kerbTop, List<Vector3> outerRing, List<bool> kerbed)
        {
            const float minimum = 0.2f;

            int count = outerRing.Count;
            var kept = new bool[count];

            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % count;

                kept[i] = kerbed[i]
                          && (outerRing[i] - kerbTop[i]).sqrMagnitude > minimum * minimum
                          && (outerRing[next] - kerbTop[next]).sqrMagnitude > minimum * minimum;
            }

            for (int i = 0; i < count; i++)
            {
                kerbed[i] = kept[i];
            }
        }

        /// <summary>
        /// One kerb return: an arc from a point on the trunk road's edge round onto a street's corner, on
        /// all three rings at once.
        ///
        /// <para>The same fillet the town's junction corners are built from, and for the same reason — a
        /// corner is square with its nose rounded off, and the nose is where the two kerb lines cross.
        /// Only the ends differ: at the street they are that street's own cross-section, so the return is
        /// flush with the ribbon by construction, and at the trunk all three rings sit on one point, which
        /// tapers the kerb to nothing and the footway to no width exactly where the trunk road's shoulder
        /// takes over.</para>
        /// </summary>
        private static void AddReturn(
            Vector3 fromGutter,
            Vector3 fromKerb,
            Vector3 fromOuter,
            Vector3 toGutter,
            Vector3 toKerb,
            Vector3 toOuter,
            Vector3 outFrom,
            Vector3 outTo,
            List<Vector3> gutter,
            List<Vector3> kerbTop,
            List<Vector3> outerRing,
            List<bool> kerbed)
        {
            // Only the interior points: both ends are already on the ring, or about to be.
            AddFillet(fromGutter, fromGutter, toGutter, outFrom, outTo, ReturnSteps, false,
                gutter, kerbed);
            AddFillet(fromKerb, fromKerb, toKerb, outFrom, outTo, ReturnSteps, false,
                kerbTop, null);
            AddFillet(fromOuter, fromOuter, toOuter, outFrom, outTo, ReturnSteps, false,
                outerRing, null);
        }


        /// <summary>
        /// The three points of a street's cross-section at one of its corners: gutter, kerb top and outer
        /// footway edge.
        ///
        /// Taken from the ribbon's own section rather than re-derived, which is what makes the mouth flush
        /// with the street to the millimetre instead of to within a tolerance — the same guarantee the
        /// pads have.
        /// </summary>
        private static Vector3 StreetCorner(
            StreetEdge edge, float handover, float acrossSign, out Vector3 kerb, out Vector3 outer)
        {
            TownStreetShape shape = edge.Shape;
            float top = shape.SurfaceLift + shape.KerbHeight;

            kerb = StreetPoint(edge, handover, (shape.HalfWidth + shape.KerbFace) * acrossSign, top);
            outer = StreetPoint(edge, handover, shape.HalfOuter * acrossSign, top);

            return StreetPoint(edge, handover, shape.HalfWidth * acrossSign, shape.SurfaceLift);
        }

        /// <summary>
        /// A point on the trunk road's <b>carriageway</b> edge — not the outer edge of its shoulder.
        ///
        /// <para>Sampled from the path at a real distance rather than interpolated between two corners, so
        /// the mouth follows the road where it curves instead of cutting the corner off. Built the way
        /// <c>RoadMeshBuilder.AppendRing</c> builds its own edge vertex, banking included, so the throat
        /// meets the asphalt exactly rather than to within the camber.</para>
        /// </summary>
        private static Vector3 TrunkEdge(
            IRoadPath trunk, in RoadShape shape, float alongTrunk, float side)
        {
            float along = Mathf.Clamp(alongTrunk, 0f, trunk.Length);

            Vector3 centre = trunk.GetPositionAtDistance(along);
            Vector3 right = trunk.GetBankedRightAtDistance(
                along, shape.MaxBankDegrees, shape.FullBankRadius);

            Vector3 up = Vector3.Cross(trunk.GetDirectionAtDistance(along), right).normalized;
            if (up.y < 0f)
            {
                up = -up;
            }

            return centre + up * shape.SurfaceLift + right * (shape.HalfWidth * side);
        }

        /// <summary>A point across the street's cross-section, where the mouth hands over to the ribbon.</summary>
        private static Vector3 StreetPoint(StreetEdge edge, float at, float across, float rise)
        {
            return TownStreetBuilder.PointAcross(edge.Path, edge.Shape, at, across, rise);
        }




    }
}
