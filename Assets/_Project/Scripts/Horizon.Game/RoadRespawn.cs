using Horizon.Vehicle;
using Horizon.World;
using UnityEngine;

namespace Horizon.Game
{
    /// <summary>
    /// Finds the nearest place on a road to put a car back on, and which way it should face there.
    ///
    /// <para>Lifted out of <c>PauseMenu</c> when the water became something a car could drive into. The
    /// menu's Respawn button and a drowned engine want exactly the same answer, and two copies of a
    /// search this fiddly would be two copies to keep in step — the wheel-radius lift alone is the kind
    /// of detail that gets fixed in one of them and not the other.</para>
    /// </summary>
    public static class RoadRespawn
    {
        /// <summary>How coarsely each lane is walked before the answer is refined.</summary>
        private const float CoarseStep = 20f;

        /// <summary>
        /// The nearest point on any driveable road, and which way it faces there.
        ///
        /// <para>Searched in the baked traffic routes rather than against the courses, because that
        /// asset already <i>is</i> every road in the world as a set of world-space polylines — the pass,
        /// both carriageways, the slip road and three hundred streets — sampled and ready. Re-deriving
        /// that from the course tables would be the same search over data that has to be rebuilt
        /// first.</para>
        ///
        /// <para>Coarse sweep then a local refinement. It runs on a button press or on a car going
        /// under, not per frame, so the cost is a few thousand distance checks once — but a lane can be
        /// a kilometre long, and testing every sample of every lane would be a hundred thousand.</para>
        /// </summary>
        /// <param name="ride">
        /// How far to lift the car above the lane. A lane polyline lies on the tarmac, and a car
        /// dropped with its origin there starts the frame with its suspension through the road.
        /// </param>
        public static bool TryNearest(
            TrafficNetwork routes,
            Vector3 from,
            float ride,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (routes == null || routes.LaneCount == 0)
            {
                return false;
            }

            int bestLane = -1;
            float bestAt = 0f;
            float bestSqr = float.MaxValue;

            for (int lane = 0; lane < routes.LaneCount; lane++)
            {
                // Connectors excluded: they are the turns through a junction, and being put down in the
                // middle of an intersection facing across it is a worse place to restart than the
                // straight a few metres away.
                if (routes.NodeOf(lane) >= 0)
                {
                    continue;
                }

                float length = routes.LengthOf(lane);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / CoarseStep));

                for (int i = 0; i <= steps; i++)
                {
                    float at = length * i / steps;
                    routes.GetLane(lane, at, out Vector3 point, out Vector3 _);

                    float sqr = (point - from).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        bestLane = lane;
                        bestAt = at;
                    }
                }
            }

            if (bestLane < 0)
            {
                return false;
            }

            float span = routes.LengthOf(bestLane);
            for (float window = CoarseStep * 0.5f; window > 0.5f; window *= 0.5f)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    float at = Mathf.Clamp(bestAt + window * side, 0f, span);
                    routes.GetLane(bestLane, at, out Vector3 point, out Vector3 _);

                    float sqr = (point - from).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        bestAt = at;
                    }
                }
            }

            routes.GetLane(bestLane, bestAt, out position, out Vector3 forward);

            position += Vector3.up * ride;
            rotation = Quaternion.LookRotation(forward, Vector3.up);
            return true;
        }

        /// <summary>How far a car's origin has to sit above the tarmac for its wheels to rest on it.</summary>
        public static float RideHeight(VehicleController vehicle)
        {
            return vehicle != null && vehicle.Config != null
                ? vehicle.Config.SuspensionRestLength + vehicle.Config.WheelRadius + 0.05f
                : 0.75f;
        }
    }
}
