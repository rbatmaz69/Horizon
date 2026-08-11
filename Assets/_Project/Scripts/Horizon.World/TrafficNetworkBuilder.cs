using System.Collections.Generic;
using UnityEngine;

namespace Horizon.World
{
    /// <summary>
    /// Turns a finished <see cref="StreetNetwork"/> into the lanes and turn connectors ambient traffic
    /// drives on.
    ///
    /// <para>Runs at edit time, after <see cref="StreetJunctionBuilder.ResolveTrims"/>, because a lane
    /// runs between the same trim points the ribbon does — the stretch between the trim point and the
    /// node belongs to the junction pad, and that is exactly the stretch a turn connector covers.</para>
    ///
    /// <para>In <c>Horizon.World</c> alongside the street graph it reads, not in the editor assembly:
    /// nothing here touches <c>UnityEditor</c>, and the lane maths is the sort of thing a route validator
    /// and a runtime director should both be able to see. Saving the result as an asset is the setup
    /// tool's business.</para>
    /// </summary>
    public static class TrafficNetworkBuilder
    {
        /// <summary>Target spacing between lane samples, metres.</summary>
        private const float SampleSpacing = 2.5f;

        /// <summary>
        /// How far from the centreline a lane runs, as a fraction of the carriageway's half-width.
        ///
        /// Half, which puts a car in the middle of its own side. Not further out: town streets here are
        /// 6.8 m wide kerb to kerb at their narrowest, and a lane at 0.75 would put a 2.3 m car's flank
        /// within a handspan of the kerb on every alley in the place.
        /// </summary>
        private const float LaneOffset = 0.5f;

        /// <summary>
        /// A lane is lifted this far above the carriageway it follows, metres.
        ///
        /// The director adds the car's own ride height on top; this is only enough to keep a baked
        /// polyline off a surface that is itself interpolated, so a validator comparing the two is not
        /// measuring rounding.
        /// </summary>
        private const float SurfaceClearance = 0.02f;

        /// <summary>
        /// Builds the routes.
        ///
        /// <para>Two directed lanes per street, and one connector per legal turn at every junction —
        /// including the U-turn where a junction has only one street on it, because a dead end and a
        /// town entrance both have to send a car back the way it came or strand it. Everywhere else the
        /// U-turn is excluded: a car that arrives at a crossroads and reverses direction reads as a
        /// mistake even when it is legal.</para>
        /// </summary>
        public static TrafficNetwork Build(StreetNetwork network)
        {
            var points = new List<Vector3>(4096);
            var laneStart = new List<int> { 0 };
            var laneLength = new List<float>();
            var laneStep = new List<float>();
            var laneNode = new List<int>();

            // Street lanes first, two per edge, so lane 2i is edge i travelled forwards and 2i + 1 is the
            // same edge travelled back. The director never needs that fact; the validator does.
            var laneEntry = new List<int>(network.Edges.Count * 2);
            var laneExitNode = new List<int>(network.Edges.Count * 2);

            for (int i = 0; i < network.Edges.Count; i++)
            {
                StreetEdge edge = network.Edges[i];

                AddStreetLane(edge, true, points, laneStart, laneLength, laneStep, laneNode);
                laneEntry.Add(edge.FromNode);
                laneExitNode.Add(edge.ToNode);

                AddStreetLane(edge, false, points, laneStart, laneLength, laneStep, laneNode);
                laneEntry.Add(edge.ToNode);
                laneExitNode.Add(edge.FromNode);
            }

            int streetLanes = laneLength.Count;

            // Then a connector for every legal turn. Collected per incoming lane so the exit table can be
            // written as prefix offsets afterwards, which needs the counts first.
            var connectorsOf = new List<int>[streetLanes];
            var connectorTarget = new List<int>(streetLanes * 2);

            for (int lane = 0; lane < streetLanes; lane++)
            {
                int node = laneExitNode[lane];
                int edgeOfLane = lane >> 1;

                for (int other = 0; other < streetLanes; other++)
                {
                    // A turn leaves the node this lane arrives at.
                    if (laneEntry[other] != node)
                    {
                        continue;
                    }

                    bool uTurn = (other >> 1) == edgeOfLane;
                    if (uTurn && network.Nodes[node].Degree > 1)
                    {
                        continue;
                    }

                    int connector = laneLength.Count;
                    AddConnector(network, node, lane, other, points, laneStart, laneLength, laneStep,
                        laneNode);

                    (connectorsOf[lane] ??= new List<int>(3)).Add(connector);
                    connectorTarget.Add(other);
                }
            }

            // The exit table. A street lane's exits are its connectors; a connector's single exit is the
            // street lane it feeds. Same shape as everything else here — counts, offsets, items.
            var exitStart = new List<int>(laneLength.Count + 1) { 0 };
            var exits = new List<int>(laneLength.Count * 2);

            for (int lane = 0; lane < streetLanes; lane++)
            {
                List<int> found = connectorsOf[lane];
                if (found != null)
                {
                    exits.AddRange(found);
                }

                exitStart.Add(exits.Count);
            }

            for (int i = 0; i < connectorTarget.Count; i++)
            {
                exits.Add(connectorTarget[i]);
                exitStart.Add(exits.Count);
            }

            var asset = ScriptableObject.CreateInstance<TrafficNetwork>();
            asset.Fill(
                points.ToArray(), laneStart.ToArray(), laneLength.ToArray(), laneStep.ToArray(),
                laneNode.ToArray(), exitStart.ToArray(), exits.ToArray(), network.Nodes.Count);

            return asset;
        }

        /// <summary>
        /// One directed lane down one street, between its two trim points.
        ///
        /// Offset to the <i>direction of travel's</i> right, which for the backwards lane is the path's
        /// own left. Getting that sign from the side the town is on, or from which end of the edge the
        /// lane starts, is the class of mistake that puts half the traffic in the oncoming lane and looks
        /// almost right.
        /// </summary>
        private static void AddStreetLane(
            StreetEdge edge,
            bool forward,
            List<Vector3> points,
            List<int> laneStart,
            List<float> laneLength,
            List<float> laneStep,
            List<int> laneNode)
        {
            float from = forward ? edge.TrimStart : edge.Length - edge.TrimEnd;
            float to = forward ? edge.Length - edge.TrimEnd : edge.TrimStart;

            float span = Mathf.Abs(to - from);
            int count = Mathf.Max(2, Mathf.CeilToInt(span / SampleSpacing) + 1);

            float across = edge.Shape.HalfWidth * LaneOffset * (forward ? 1f : -1f);
            float rise = edge.Shape.SurfaceLift + SurfaceClearance;

            for (int i = 0; i < count; i++)
            {
                float at = Mathf.Lerp(from, to, i / (float)(count - 1));
                points.Add(TownStreetBuilder.PointAcross(edge.Path, edge.Shape, at, across, rise));
            }

            laneStart.Add(points.Count);
            laneLength.Add(span);
            laneStep.Add(span / (count - 1));
            laneNode.Add(-1);
        }

        /// <summary>
        /// A turn through a junction: a quadratic from where one lane ends, bent about the node centre,
        /// to where the next one starts.
        ///
        /// <para>A quadratic Bézier passes through both its endpoints exactly, which is the whole reason
        /// for using one — a connector is flush with the lanes it joins by construction rather than to
        /// within a tolerance, and the validator checks that rather than assuming it. The node centre is
        /// the control point, not a point on the curve, so the turn cuts the corner the way a car does
        /// instead of driving to the middle of the junction and back out.</para>
        /// </summary>
        private static void AddConnector(
            StreetNetwork network,
            int node,
            int fromLane,
            int toLane,
            List<Vector3> points,
            List<int> laneStart,
            List<float> laneLength,
            List<float> laneStep,
            List<int> laneNode)
        {
            Vector3 start = LastPointOf(points, laneStart, fromLane);
            Vector3 end = FirstPointOf(points, laneStart, toLane);

            Vector3 control = network.Nodes[node].Position;
            control.y = (start.y + end.y) * 0.5f;

            // Length from the chord rather than by integrating the curve: a turn connector is ten metres
            // of gentle arc, the two differ by a few per cent, and every consumer of this number is
            // either a speed or a look-ahead.
            float chord = Vector3.Distance(start, control) + Vector3.Distance(control, end);
            int count = Mathf.Max(3, Mathf.CeilToInt(chord / SampleSpacing) + 1);

            var previous = start;
            float measured = 0f;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                float inverse = 1f - t;

                Vector3 on = inverse * inverse * start + 2f * inverse * t * control + t * t * end;
                points.Add(on);

                if (i > 0)
                {
                    measured += Vector3.Distance(previous, on);
                }

                previous = on;
            }

            laneStart.Add(points.Count);
            laneLength.Add(measured);
            laneStep.Add(measured / (count - 1));
            laneNode.Add(node);
        }

        private static Vector3 FirstPointOf(List<Vector3> points, List<int> laneStart, int lane)
        {
            return points[laneStart[lane]];
        }

        private static Vector3 LastPointOf(List<Vector3> points, List<int> laneStart, int lane)
        {
            return points[laneStart[lane + 1] - 1];
        }
    }
}
